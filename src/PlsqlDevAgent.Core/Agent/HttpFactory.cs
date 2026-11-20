using System;
using System.Net;
using System.Net.Http;
using PlsqlDevAgent.Util;

namespace PlsqlDevAgent.Agent
{
    /// <summary>
    /// HTTP settings shared by the LLM client and the MCP clients: proxy, certificate check (Windows + trusted
    /// thumbprints), connection limits and readable connection errors.
    /// </summary>
    internal static class HttpFactory
    {
        /// <summary>
        /// A handler for one kind of server. The Windows login goes to the company proxy only, never to the server itself
        /// unless <paramref name="windowsLogin"/> is set (explicit opt-in for a server that needs it): otherwise a server or
        /// an attacker in between could request NTLM/Kerberos authentication and capture it.
        /// </summary>
        public static HttpClientHandler Handler(bool useSystemProxy, string trustedCertificates, bool windowsLogin = false)
        {
            var handler = new HttpClientHandler { UseProxy = useSystemProxy, UseDefaultCredentials = windowsLogin };
            if (useSystemProxy) handler.DefaultProxyCredentials = CredentialCache.DefaultCredentials;
            var pins = Certificates.ParseThumbprints(trustedCertificates);
            handler.ServerCertificateCustomValidationCallback = (request, cert, chain, errors) =>
                Certificates.Validate(request.RequestUri, cert, chain, errors, pins);
            return handler;
        }

        /// <summary>A readable message for connection errors (certificate and TLS problems instead of .NET's generic text).</summary>
        public static string DescribeConnectionError(string url, HttpRequestException ex)
        {
            var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "";
            var problem = Certificates.LastProblem(host);
            if (problem != null) return L.T("The HTTPS certificate of {0} is not accepted: {1}", host, problem);
            if (ex.InnerException is WebException we && we.Status == WebExceptionStatus.SecureChannelFailure)
                return L.T("Could not establish a secure (TLS) connection to {0}. The server may require a TLS version or cipher that Windows on this PC does not offer. ({1})",
                    url, ex.GetBaseException().Message);
            return L.T("Could not reach {0}: {1}", url, ex.GetBaseException().Message);
        }

        /// <summary>
        /// .NET Framework allows only 2 parallel connections per server for desktop programs, which would queue parallel
        /// requests (helper agents, parallel tool calls). Raised for the given server only (not process wide).
        /// </summary>
        public static void AllowParallelRequests(string url)
        {
            try
            {
                var sp = ServicePointManager.FindServicePoint(new Uri(url));
                if (sp.ConnectionLimit < 16) sp.ConnectionLimit = 16;
                sp.UseNagleAlgorithm = false; // small request bodies are sent at once instead of waiting for an ACK
            }
            catch (Exception ex) { Log.Warn("Could not configure the connection to " + url + ": " + ex.Message); }
        }
    }
}
