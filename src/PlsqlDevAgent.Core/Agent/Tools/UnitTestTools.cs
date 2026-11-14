using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using Newtonsoft.Json.Linq;
using PlsqlDevAgent.Ide;
using PlsqlDevAgent.Util;
using static PlsqlDevAgent.Agent.SqlUtil;

namespace PlsqlDevAgent.Agent.Tools
{
    /// <summary>utPLSQL v3 in the connected database (package UT, usually in schema UT3 with public synonyms).</summary>
    internal static class UtPlsql
    {
        public sealed class Installation
        {
            public string Owner, Version;
            /// <summary>Owner-qualified name of a utPLSQL object, e.g. "UT3".ut.</summary>
            public string Q(string name) => $"\"{Owner}\".{name}";
        }

        /// <summary>The utPLSQL installation the current user can call, null if there is none.</summary>
        public static Installation Detect(IIde ide)
        {
            try
            {
                var owners = ide.Query(
                    "select owner from all_objects where object_name = 'UT' and object_type = 'PACKAGE' order by decode(owner, 'UT3', 0, 1), owner", 5);
                foreach (var row in owners.Rows)
                {
                    try
                    {
                        var v = ide.Query($"select \"{row[0]}\".ut.version() from dual", 1);
                        // e.g. "v.3.2.3.4508" (3.2) or "v3.1.13.3829"
                        if (v.Rows.Count > 0 && Regex.IsMatch(v.Rows[0][0] ?? "", @"^v?\.?\d")) return new Installation { Owner = row[0], Version = v.Rows[0][0] };
                    }
                    catch (OracleSqlException) { } // another package named UT, or no execute privilege
                }
            }
            catch (OracleSqlException ex) { Log.Warn("utPLSQL detection failed: " + ex.Message); }
            return null;
        }
    }

    internal sealed class RunUnitTestsTool : AgentTool
    {
        public override string Name => "run_unit_tests";
        public override string Description =>
            "Runs utPLSQL v3 unit tests (ut.run) and returns the report: passed, failed and errored tests with the failed " +
            "expectations and error stacks. path: a test package (UT_EMP_API), OWNER.PACKAGE, OWNER.PACKAGE.PROCEDURE, a " +
            "suite path (:app.billing) or a schema for all its tests (default: the current schema). coverage_objects: also " +
            "report which lines of these program units the tests executed (code coverage). utPLSQL rolls back what the tests " +
            "change, except tests annotated --%rollback(manual). The user must approve.";
        public override JObject Parameters => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["path"] = new JObject { ["type"] = "string", ["description"] = "What to run (see above). Several paths separated by commas." },
                ["coverage_objects"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "string" },
                    ["description"] = "Program units (NAME or OWNER.NAME) to measure code coverage for, usually the units under test. Runs the tests a second time.",
                },
            },
        };
        public override bool Mutating => true;
        public override string Summarize(JObject args) =>
            (Str(args, "path") ?? "current schema") + (args?["coverage_objects"] is JArray a && a.Count > 0 ? " (coverage)" : "");

        private static readonly Regex Summary = new Regex(
            @"(?<tests>\d+) tests?, (?<failed>\d+) failed, (?<errored>\d+) errored, (?<disabled>\d+) disabled, (?<warnings>\d+) warning", RegexOptions.Compiled);

        public override async Task<string> ExecuteAsync(JObject args, ToolContext ctx)
        {
            var ut = UtPlsql.Detect(ctx.Ide);
            if (ut == null)
                return "utPLSQL is not installed in this database (or this user cannot execute it): there is no package UT. " +
                       "Write the test as an anonymous PL/SQL block that checks the results and raises an error on a mismatch, and " +
                       "open it in a Test window (open_new_window with window_type test) for the user to run. utPLSQL itself: " +
                       "https://github.com/utPLSQL/utPLSQL (installed by a DBA).";

            var path = Str(args, "path");
            path = string.IsNullOrWhiteSpace(path) ? ctx.CurrentSchema : path.Trim();
            var coverage = (args?["coverage_objects"] as JArray)?.Select(t => ((string)t ?? "").Trim()).Where(s => s.Length > 0)
                .Select(s => s.StartsWith("\"") ? s : s.ToUpperInvariant()).Take(50).ToList() ?? new List<string>();

            var run = $"begin\n  {ut.Q("ut.run")}(a_path => {Lit(path)}, a_color_console => false);\nend;";
            var coverageRun = coverage.Count == 0 ? null :
                $"begin\n  {ut.Q("ut.run")}(a_path => {Lit(path)}, a_reporter => {ut.Q("ut_coverage_cobertura_reporter")}(),\n" +
                $"    a_include_objects => {ut.Q("ut_varchar2_list")}({string.Join(", ", coverage.Select(Lit))}));\nend;";

            if (!ctx.Config.AutoApproveSql)
            {
                var approval = await ctx.ApproveAsync(new ApprovalRequest
                {
                    Category = "test",
                    Title = L.T("Run unit tests ({0}): {1}", ctx.Ide.GetContext().ConnectionLabel, path),
                    Detail = run + (coverageRun == null ? "" : "\n/\n" + coverageRun),
                });
                if (!approval.Approved) return approval.RejectionMessage;
            }

            var res = ctx.Ide.Execute(run);
            var report = (res.DbmsOutput ?? "").Replace("\r\n", "\n").TrimEnd();
            if (!res.Ok)
            {
                // ORA-20204: suite not found; ORA-44001: a single name that is neither a package nor a schema
                var missing = res.ErrorCode == 20204 || res.ErrorCode == 44001 || Regex.IsMatch(res.ErrorMessage ?? "", "ORA-(20204|44001)");
                var why = missing ? DiagnoseMissingSuite(ctx, path) : null;
                return $"The utPLSQL run failed: {res.ErrorMessage.Trim()}" + (report.Length > 0 ? "\n\nOutput:\n" + report : "") +
                       "\n" + (why ?? "Check the path (test package name, suite path) and that the test package compiled without errors.");
            }

            var sb = new StringBuilder();
            var m = Summary.Match(report);
            string invalidNote = null;
            if (m.Success)
            {
                int failed = int.Parse(m.Groups["failed"].Value), errored = int.Parse(m.Groups["errored"].Value);
                sb.Append($"utPLSQL {ut.Version}: {m.Groups["tests"].Value} tests, {failed} failed, {errored} errored, " +
                          $"{m.Groups["disabled"].Value} disabled, {m.Groups["warnings"].Value} warnings – " +
                          (failed + errored == 0 ? "ALL PASSED" : "NOT PASSED") + ".\n\n");
                // errored because something is invalid (the test package or the code under test): say what
                if (errored > 0 && Regex.IsMatch(report, @"does not exist or is invalid|ORA-04063|ORA-06508|ORA-04065|PLS-00905", RegexOptions.IgnoreCase))
                    invalidNote = DiagnoseInvalid(ctx, path);
            }
            else
            {
                sb.Append($"utPLSQL {ut.Version}: no test summary in the output. If no tests were found: a test package needs the " +
                          "--%suite annotation in its specification and --%test before each test procedure, and must compile without errors.\n\n");
            }
            sb.Append(report.Length == 0 ? "(no output)" : report);
            if (invalidNote != null) sb.Append("\n\nWhy the tests errored:\n").Append(invalidNote);

            if (coverageRun != null)
            {
                var cov = ctx.Ide.Execute(coverageRun);
                sb.Append("\n\nCode coverage: ");
                if (!cov.Ok) sb.Append("failed: ").Append(cov.ErrorMessage.Trim());
                else sb.Append(CoverageSummary(cov.DbmsOutput) ?? "no coverage data (" + Db.Truncate((cov.DbmsOutput ?? "").Trim(), 500) + ")");
            }
            return Db.Truncate(sb.ToString(), ctx.Config.MaxToolOutputChars);
        }

        /// <summary>
        /// utPLSQL's ORA-20204 ("suite package … does not exist") has several causes; say which one it is: the package
        /// does not exist, it is invalid (with its errors), or its specification has no --%suite annotation.
        /// </summary>
        private static string DiagnoseMissingSuite(ToolContext ctx, string path)
        {
            try
            {
                var notes = new List<string>();
                foreach (var part in path.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0 && !p.StartsWith(":")))
                {
                    var names = part.Split('.').Select(Ident).ToArray();
                    // OWNER.PACKAGE[.PROCEDURE] or PACKAGE[.PROCEDURE]
                    string owner = ctx.CurrentSchema, package = names[0];
                    if (names.Length >= 2 && ctx.Ide.Query($"select 1 from all_users where username = {Lit(names[0])}", 1).Rows.Count > 0)
                    {
                        owner = names[0];
                        package = names[1];
                    }
                    var status = ctx.Ide.Query(
                        $"select object_type, status from all_objects where owner = {Lit(owner)} and object_name = {Lit(package)} " +
                        "and object_type in ('PACKAGE', 'PACKAGE BODY') order by object_type", 2);
                    if (status.Rows.Count == 0)
                    {
                        // a schema name alone runs all suites of the schema
                        if (names.Length == 1 && ctx.Ide.Query($"select 1 from all_users where username = {Lit(names[0])}", 1).Rows.Count > 0)
                            notes.Add($"Schema {names[0]} has no test packages with a --%suite annotation (or they are invalid).");
                        else notes.Add($"There is no package {owner}.{package}: create the test package first (compile_plsql) or check the name.");
                        continue;
                    }
                    var invalid = status.Rows.Where(r => r[1] == "INVALID").Select(r => r[0]).ToList();
                    if (invalid.Count > 0)
                    {
                        var errors = (Db.Errors(ctx, owner, package, "PACKAGE") ?? "") + (Db.Errors(ctx, owner, package, "PACKAGE BODY") ?? "");
                        notes.Add($"{string.Join(" and ", invalid)} {owner}.{package} is INVALID, and utPLSQL ignores invalid test packages. " +
                                  (errors.Length > 0 ? "Fix these errors first:\n" + errors.TrimEnd()
                                                     : "It has no errors of its own: an object it uses (e.g. the package under test) is invalid. Check with recompile_invalid."));
                        continue;
                    }
                    // the --%suite line and the line after it (utPLSQL needs an empty line between package and procedure annotations)
                    var suite = ctx.Ide.Query(
                        "select s.line, (select trim(n.text) from all_source n where n.owner = s.owner and n.name = s.name and n.type = s.type and n.line = s.line + 1) next_line " +
                        $"from all_source s where s.owner = {Lit(owner)} and s.name = {Lit(package)} and s.type = 'PACKAGE' " +
                        "and regexp_like(s.text, '--[[:space:]]*%suite', 'i') order by s.line", 1);
                    if (suite.Rows.Count == 0)
                        notes.Add($"The specification of {owner}.{package} has no --%suite annotation. utPLSQL only finds packages whose SPECIFICATION " +
                                  "contains a --%suite(description) comment line and --%test before each test procedure.");
                    else if (!string.IsNullOrWhiteSpace(suite.Rows[0][1]) && suite.Rows[0][1].TrimStart().StartsWith("--%"))
                        notes.Add($"In {owner}.{package} the --%suite annotation (line {suite.Rows[0][0]}) is directly followed by another annotation " +
                                  $"('{suite.Rows[0][1].Trim()}'). utPLSQL then does not see the suite: put an EMPTY LINE after the --%suite line " +
                                  "(package annotations must be separated from the --%test annotation of the first procedure) and compile the specification again.");
                    else
                        notes.Add($"{owner}.{package} is valid and has a --%suite annotation: check the path (for OWNER.PACKAGE.PROCEDURE the procedure " +
                                  "needs a --%test annotation directly above it).");
                }
                return notes.Count == 0 ? null : string.Join("\n", notes);
            }
            catch (OracleSqlException ex)
            {
                Log.Warn("Suite diagnosis failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Tests errored with "is invalid" / ORA-04063: the test package's own errors and the invalid objects it uses
        /// (typically the package under test, e.g. left invalid by a compile error).
        /// </summary>
        private static string DiagnoseInvalid(ToolContext ctx, string path)
        {
            try
            {
                var notes = new List<string>();
                foreach (var part in path.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0 && !p.StartsWith(":")))
                {
                    var names = part.Split('.').Select(Ident).ToArray();
                    string owner = ctx.CurrentSchema, package = names[0];
                    if (names.Length >= 2 && ctx.Ide.Query($"select 1 from all_users where username = {Lit(names[0])}", 1).Rows.Count > 0)
                    {
                        owner = names[0];
                        package = names[1];
                    }
                    var own = (Db.Errors(ctx, owner, package, "PACKAGE") ?? "") + (Db.Errors(ctx, owner, package, "PACKAGE BODY") ?? "");
                    if (own.Length > 0) notes.Add($"The test package {owner}.{package} has compile errors:\n{own.TrimEnd()}");
                    // invalid objects the test package uses, including the bodies of packages it calls
                    var used = ctx.Ide.Query(
                        "select o.object_type, o.owner, o.object_name from all_objects o where o.status = 'INVALID' " +
                        "and (o.owner, o.object_name) in (select d.referenced_owner, d.referenced_name from all_dependencies d " +
                        $"where d.owner = {Lit(owner)} and d.name = {Lit(package)} and not (d.referenced_owner = d.owner and d.referenced_name = d.name)) " +
                        "order by o.owner, o.object_name, o.object_type", 20);
                    foreach (var row in used.Rows)
                    {
                        var errors = Db.Errors(ctx, row[1], row[2], row[0]);
                        notes.Add($"{row[0]} {row[1]}.{row[2]} used by the tests is INVALID" +
                                  (errors != null ? ":\n" + errors.TrimEnd() : " (no errors of its own: something it uses is invalid; recompile_invalid shows more)."));
                    }
                }
                return notes.Count == 0 ? null : string.Join("\n", notes) + "\nFix these compile errors first (in the code under test only if the user agreed), then run the tests again.";
            }
            catch (OracleSqlException ex)
            {
                Log.Warn("Invalid-object diagnosis failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>Cobertura XML of utPLSQL -> "SCOTT.EMP_API: 85% of 40 lines executed, not executed: 12-14, 30".</summary>
        internal static string CoverageSummary(string output)
        {
            var text = output ?? "";
            int start = text.IndexOf("<?xml", StringComparison.Ordinal);
            if (start < 0) start = text.IndexOf("<coverage", StringComparison.Ordinal);
            if (start < 0) return null;
            var lines = new StringBuilder();
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
                var doc = new XmlDocument { XmlResolver = null };
                using (var reader = XmlReader.Create(new StringReader(text.Substring(start)), settings)) doc.Load(reader);
                foreach (XmlElement cls in doc.GetElementsByTagName("class"))
                {
                    var all = cls.GetElementsByTagName("line").Cast<XmlElement>()
                        .Select(l => (Number: int.Parse(l.GetAttribute("number")), Hits: long.TryParse(l.GetAttribute("hits"), out var h) ? h : 0))
                        .ToList();
                    var missed = all.Where(l => l.Hits == 0).Select(l => l.Number).OrderBy(n => n).ToList();
                    int covered = all.Count - missed.Count;
                    var percent = all.Count == 0 ? "no executable lines" : $"{100.0 * covered / all.Count:0}% of {all.Count} lines executed";
                    // utPLSQL writes "package body scott.emp_api": shown like the other tools, "PACKAGE BODY SCOTT.EMP_API"
                    lines.Append($"\n- {cls.GetAttribute("filename").ToUpperInvariant()}: {percent}");
                    if (missed.Count > 0) lines.Append(missed.Count == 1 ? ", not executed: line " : ", not executed: lines ").Append(Ranges(missed));
                }
            }
            catch (XmlException ex)
            {
                return "the coverage report could not be read (" + ex.Message + ")";
            }
            return lines.Length == 0 ? "the listed units were not executed by the tests (or are not in the tests' schema)" : lines.ToString().TrimStart('\n');
        }

        private static string Ranges(List<int> numbers)
        {
            var parts = new List<string>();
            for (int i = 0; i < numbers.Count; i++)
            {
                int first = numbers[i];
                while (i + 1 < numbers.Count && numbers[i + 1] == numbers[i] + 1) i++;
                parts.Add(numbers[i] == first ? first.ToString() : $"{first}-{numbers[i]}");
                if (parts.Count == 40) { parts.Add("…"); break; }
            }
            return string.Join(", ", parts);
        }
    }
}
