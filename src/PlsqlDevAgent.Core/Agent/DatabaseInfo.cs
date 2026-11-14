using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PlsqlDevAgent.Ide;

namespace PlsqlDevAgent.Agent
{
    /// <summary>
    /// Facts about the connected database for the system prompt: Oracle version (which syntax is available),
    /// NLS settings (decimal comma, date format), character set and edition. Read once per connection.
    /// </summary>
    public sealed class DatabaseInfo
    {
        public string Product, Version;
        public int Major, Minor;
        /// <summary>NLS session parameters, e.g. NLS_NUMERIC_CHARACTERS -> ",.".</summary>
        public Dictionary<string, string> Nls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string CharacterSet, NCharacterSet, Edition;
        /// <summary>utPLSQL v3 the user can run (null = not installed or not accessible).</summary>
        public string UtPlsqlVersion, UtPlsqlOwner;

        private static readonly string[] NlsParameters =
        {
            "NLS_LANGUAGE", "NLS_TERRITORY", "NLS_DATE_FORMAT", "NLS_TIMESTAMP_FORMAT", "NLS_NUMERIC_CHARACTERS",
            "NLS_SORT", "NLS_COMP", "NLS_LENGTH_SEMANTICS",
        };

        /// <summary>Reads the facts from the agent's session; null if nothing could be read.</summary>
        public static DatabaseInfo Load(IIde ide)
        {
            var info = new DatabaseInfo();
            bool any = false;
            try
            {
                QueryResult r;
                try { r = ide.Query("select product, version_full from product_component_version where upper(product) like 'ORACLE%'", 5); }
                catch (OracleSqlException) { r = ide.Query("select product, version from product_component_version where upper(product) like 'ORACLE%'", 5); } // before 18c
                if (r.Rows.Count > 0)
                {
                    info.Product = (r.Rows[0][0] ?? "").Trim();
                    info.Version = (r.Rows[0][1] ?? "").Trim();
                    var m = Regex.Match(info.Version, @"^(\d+)\.(\d+)");
                    if (m.Success)
                    {
                        info.Major = int.Parse(m.Groups[1].Value);
                        info.Minor = int.Parse(m.Groups[2].Value);
                    }
                    any = true;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Util.Log.Warn("Database version could not be read: " + ex.Message); }

            try
            {
                var list = string.Join(", ", NlsParameters.Select(SqlUtil.Lit));
                var r = ide.Query(
                    $"select parameter, value from nls_session_parameters where parameter in ({list}) " +
                    "union all select parameter, value from nls_database_parameters where parameter in ('NLS_CHARACTERSET', 'NLS_NCHAR_CHARACTERSET') " +
                    "union all select 'EDITION', sys_context('USERENV', 'CURRENT_EDITION_NAME') from dual", 50);
                foreach (var row in r.Rows)
                {
                    var name = (row[0] ?? "").Trim();
                    var value = row[1] ?? "";
                    if (name == "NLS_CHARACTERSET") info.CharacterSet = value;
                    else if (name == "NLS_NCHAR_CHARACTERSET") info.NCharacterSet = value;
                    else if (name == "EDITION") info.Edition = value;
                    else info.Nls[name] = value;
                    any = true;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Util.Log.Warn("NLS settings could not be read: " + ex.Message); }

            if (any)
            {
                var ut = Tools.UtPlsql.Detect(ide);
                info.UtPlsqlVersion = ut?.Version;
                info.UtPlsqlOwner = ut?.Owner;
            }
            return any ? info : null;
        }

        /// <summary>The "# Connected database" section of the system prompt.</summary>
        public string PromptSection()
        {
            var sb = new StringBuilder("# Connected database\n");
            if (Major > 0)
            {
                sb.Append($"- {Product}, version {Version}. ");
                sb.Append(VersionHint()).Append('\n');
            }
            if (!string.IsNullOrEmpty(CharacterSet))
            {
                sb.Append($"- Character set {CharacterSet}");
                if (!string.IsNullOrEmpty(NCharacterSet)) sb.Append($" (national {NCharacterSet})");
                var semantics = Get("NLS_LENGTH_SEMANTICS");
                if (!string.IsNullOrEmpty(semantics)) sb.Append($", NLS_LENGTH_SEMANTICS {semantics}");
                if (string.Equals(semantics, "BYTE", StringComparison.OrdinalIgnoreCase) && CharacterSet.IndexOf("UTF8", StringComparison.OrdinalIgnoreCase) >= 0)
                    sb.Append(": VARCHAR2(n) without CHAR counts bytes, a character like ä or € takes 2-3 bytes");
                sb.Append(".\n");
            }
            if (Nls.Count > 0)
            {
                sb.Append("- NLS settings of your database session: ");
                var parts = new List<string>();
                void Add(string label, string key) { var v = Get(key); if (!string.IsNullOrEmpty(v)) parts.Add($"{label} {v}"); }
                Add("language", "NLS_LANGUAGE");
                Add("territory", "NLS_TERRITORY");
                Add("date format", "NLS_DATE_FORMAT");
                Add("timestamp format", "NLS_TIMESTAMP_FORMAT");
                var numeric = Get("NLS_NUMERIC_CHARACTERS");
                if (!string.IsNullOrEmpty(numeric)) parts.Add($"decimal and group separator \"{numeric}\"");
                Add("sort", "NLS_SORT");
                Add("comparison", "NLS_COMP");
                sb.Append(string.Join(", ", parts)).Append(".");
                if (numeric != null && numeric.StartsWith(","))
                    sb.Append(" Numbers in query results use a decimal comma (1234,5 means 1234.5), and TO_NUMBER('1.5') without a format fails in this session.");
                sb.Append(" The user's and the application's sessions may use other NLS settings: never rely on implicit conversions or NLS defaults; " +
                          "use explicit format masks in TO_CHAR / TO_DATE / TO_NUMBER and ANSI literals (DATE 'YYYY-MM-DD', TIMESTAMP '…').\n");
            }
            if (!string.IsNullOrEmpty(Edition) && !string.Equals(Edition, "ORA$BASE", StringComparison.OrdinalIgnoreCase))
                sb.Append($"- Edition-based redefinition: your session uses edition {Edition}; editionable objects you compile are created in this edition.\n");
            if (UtPlsqlVersion != null)
                sb.Append($"- Unit tests: utPLSQL {UtPlsqlVersion} is installed (schema {UtPlsqlOwner}). Write tests as utPLSQL v3 test packages " +
                          "(specification: --%suite(description), then an EMPTY LINE, then --%test(description) directly before each test procedure – " +
                          "without the empty line utPLSQL does not find the suite (ORA-20204); optional --%beforeall / " +
                          "--%beforeeach / --%throws(-20001); body: ut.expect(actual).to_equal(expected), to_be_null(), to_be_true() …), " +
                          "compile them with compile_plsql and run them with run_unit_tests (not ut.run in execute_sql: run_unit_tests explains " +
                          "why a suite is not found). Test data is inserted by the test package itself " +
                          "(--%beforeall / --%beforeeach); utPLSQL rolls it back. Never delete, change or commit existing data for a test.\n");
            else
                sb.Append("- Unit tests: utPLSQL is not installed in this database. Write tests as anonymous PL/SQL blocks that check the " +
                          "results and raise an error on a mismatch, and open them in a Test window (open_new_window with window_type test) " +
                          "for the user to run.\n");
            return sb.ToString();
        }

        private string Get(string key) => Nls.TryGetValue(key, out var v) ? v : null;

        /// <summary>Which syntax the model may use on this version.</summary>
        private string VersionHint()
        {
            const string newSyntax =
                "IF [NOT] EXISTS in DDL, the BOOLEAN data type in SQL and tables, SELECT without FROM, GROUP BY column alias or position, " +
                "UPDATE / DELETE with a FROM clause (join), the VALUES table constructor, SQL domains and annotations";
            if (Major >= 23)
                return "The features of Oracle 23ai and later are available (" + newSyntax + "). Production databases often still run an older " +
                       "version: prefer syntax that also works on 19c unless the user wants the new features or the existing code already uses them.";
            var sb = new StringBuilder("Write code for this version. Not available: " + newSyntax);
            if (Major < 12 || (Major == 12 && Minor < 2))
                sb.Append("; identifiers longer than 30 bytes; LISTAGG … ON OVERFLOW, VALIDATE_CONVERSION, CAST … DEFAULT ON CONVERSION ERROR");
            if (Major < 12)
                sb.Append("; FETCH FIRST n ROWS ONLY and OFFSET (use ROWNUM in a subquery instead), identity columns, " +
                          "default values from sequences, WITH FUNCTION, LATERAL / CROSS APPLY, the JSON functions");
            sb.Append(".");
            if (Major >= 18 || (Major == 12 && Minor >= 2)) sb.Append(" Identifiers may have up to 128 bytes, but keep to 30 if the code must also run on older databases.");
            return sb.ToString();
        }
    }
}
