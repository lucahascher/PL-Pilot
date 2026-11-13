using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using PlsqlDevAgent.Util;

namespace PlsqlDevAgent.Agent
{
    /// <summary>
    /// HTTPS certificate check for the LLM server. Windows' normal validation decides (the company CA is usually
    /// installed on managed PCs); in addition certificates can be trusted by thumbprint in the settings (the server
    /// certificate or a CA certificate in its chain), which needs no admin rights. Rejections are remembered per host
    /// so the user gets a clear message instead of .NET's generic one.
    /// </summary>
    internal static class Certificates
    {
        private static readonly ConcurrentDictionary<string, string> Problems = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Chain problems a trusted thumbprint may override (not: expired, revoked, wrong host name).</summary>
        private const X509ChainStatusFlags Overridable = X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.PartialChain |
            X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation;

        /// <summary>SHA-1 (40) or SHA-256 (64 hex digits) thumbprints, separated by ; or new lines; spaces, colons and prefixes are ignored.</summary>
        public static HashSet<string> ParseThumbprints(string text)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in (text ?? "").Split(new[] { ';', ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var part = raw.Contains("=") ? raw.Substring(raw.LastIndexOf('=') + 1) : raw; // "SHA1 Fingerprint=AB:CD…"
                var hex = Regex.Replace(part, "[^0-9A-Fa-f]", "").ToUpperInvariant();          // also drops invisible marks copied from Windows dialogs
                if (hex.Length == 40 || hex.Length == 64) set.Add(hex);
            }
            return set;
        }

        public static string LastProblem(string host) => host != null && Problems.TryGetValue(host, out var p) ? p : null;

        public static bool Validate(Uri uri, X509Certificate2 cert, X509Chain chain, SslPolicyErrors errors, HashSet<string> trusted)
        {
            var host = uri?.Host ?? "";
            if (errors == SslPolicyErrors.None) { Problems.TryRemove(host, out _); return true; }

            bool onlyChain = errors == SslPolicyErrors.RemoteCertificateChainErrors;
            bool overridable = onlyChain && (chain?.ChainStatus ?? new X509ChainStatus[0]).All(s => (s.Status & ~Overridable) == 0);
            if (overridable && trusted.Count > 0 && ChainCertificates(cert, chain).Any(c => trusted.Contains(c.Thumbprint.ToUpperInvariant()) || trusted.Contains(Sha256(c))))
            {
                Problems.TryRemove(host, out _);
                return true;
            }
            var problem = Describe(host, cert, chain, errors);
            Problems[host] = problem;
            Log.Warn($"HTTPS certificate of {host} rejected ({errors}): {problem}");
            return false;
        }

        private static string Describe(string host, X509Certificate2 cert, X509Chain chain, SslPolicyErrors errors)
        {
            if (cert == null || (errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0) return L.T("the server sent no certificate");
            var parts = new List<string>();
            if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
                parts.Add(L.T("it is issued for {0}, not for {1}: use that name in the Base URL", Names(cert), host));
            if ((errors & SslPolicyErrors.RemoteCertificateChainErrors) != 0)
            {
                var flags = (chain?.ChainStatus ?? new X509ChainStatus[0]).Aggregate((X509ChainStatusFlags)0, (a, s) => a | s.Status);
                if ((flags & X509ChainStatusFlags.NotTimeValid) != 0)
                    parts.Add(L.T("it is expired or not yet valid (valid from {0:d} to {1:d})", cert.NotBefore, cert.NotAfter));
                if ((flags & X509ChainStatusFlags.Revoked) != 0) parts.Add(L.T("it was revoked"));
                if ((flags & (X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.PartialChain)) != 0)
                {
                    var top = ChainCertificates(cert, chain).Last();
                    parts.Add(L.T("Windows on this PC does not trust its issuer \"{0}\" (self-signed, or the company's root certificate is not installed here)",
                        top.GetNameInfo(X509NameType.SimpleName, true)));
                }
                var other = flags & ~(X509ChainStatusFlags.NotTimeValid | X509ChainStatusFlags.Revoked | X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.PartialChain);
                if (other != 0) parts.Add(string.Join(", ", chain.ChainStatus.Where(s => (s.Status & other) != 0).Select(s => s.StatusInformation.Trim()).Distinct()));
                if ((flags & ~Overridable) == 0 && (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0)
                    parts.Add(L.T("After checking with your IT that it is the right certificate, you can trust it on this PC (no admin rights needed) under Settings → Trusted Certificates with one of these thumbprints: {0}",
                        string.Join("; ", ChainCertificates(cert, chain).Select(c => $"{c.GetNameInfo(X509NameType.SimpleName, false)} = {c.Thumbprint}"))));
            }
            return string.Join("; ", parts);
        }

        /// <summary>Host names the certificate is valid for (subject alternative names, else the common name).</summary>
        private static string Names(X509Certificate2 cert)
        {
            var san = cert.Extensions["2.5.29.17"]?.Format(false);
            if (!string.IsNullOrWhiteSpace(san))
                return string.Join(", ", Regex.Matches(san, @"(?:DNS[- ]Name|IP Address)=([^,\r\n]+)").Cast<Match>().Select(m => m.Groups[1].Value.Trim()).DefaultIfEmpty(san));
            return cert.GetNameInfo(X509NameType.SimpleName, false);
        }

        private static List<X509Certificate2> ChainCertificates(X509Certificate2 cert, X509Chain chain)
        {
            var list = chain?.ChainElements.Cast<X509ChainElement>().Select(e => e.Certificate).ToList() ?? new List<X509Certificate2>();
            if (list.Count == 0 && cert != null) list.Add(cert);
            return list;
        }

        private static string Sha256(X509Certificate2 c)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(c.RawData)).Replace("-", "");
        }
    }
}
