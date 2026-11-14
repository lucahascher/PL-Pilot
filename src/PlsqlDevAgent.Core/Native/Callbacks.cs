using System;
using System.Runtime.InteropServices;
using System.Text;

namespace PlsqlDevAgent.Native
{
    /// <summary>
    /// Typed access to the PL/SQL Developer callback functions (see PlugInDoc\plugindoc.pdf).
    /// The native shim stores every address passed to RegisterCallback in a table indexed by
    /// callback number; here we turn the ones we need into delegates.
    /// All of these MUST be called on the PL/SQL Developer main thread (use Ide.MainThread).
    /// Strings use the Windows ANSI code page (see <see cref="Enc"/>).
    /// </summary>
    internal static class Callbacks
    {
        private static IntPtr _table;

        // ---- delegate types (cdecl; BOOL = 32 bit int) ----
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int IntFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr StrFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr StrFnInt(int i);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int IntFnStr(IntPtr s);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int IntFnInt(int i);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int IntFnIntStr(int i, IntPtr s);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void VoidFnInt(int i);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void VoidFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void VoidFnStr(IntPtr s);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void VoidFnIntInt(int a, int b);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void VoidFnIntStrInt(int type, IntPtr text, int execute);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void VoidFnOut3(out IntPtr a, out IntPtr b, out IntPtr c);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void VoidFnOut4(out IntPtr a, out IntPtr b, out IntPtr c, out IntPtr d);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int IntFnOut4(out IntPtr a, out IntPtr b, out IntPtr c, out IntPtr d);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr StrFnStr3(IntPtr a, IntPtr b, IntPtr c);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr StrFnStr1(IntPtr a);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr StrFnIntInt(int a, int b);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void VoidFnStr3Int(IntPtr a, IntPtr b, IntPtr c, int action);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int IntFnStr4(IntPtr a, IntPtr b, IntPtr c, IntPtr d);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void VoidFnIntStr4(int id, IntPtr a, IntPtr b, IntPtr c, IntPtr d);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void VoidFnOutIntOutStr(out int a, out IntPtr b);

        // ---- SYS ----
        public static IntFn SYS_Version;                        // 1
        public static StrFn SYS_RootDir;                        // 3
        // ---- IDE ----
        public static IntFn IDE_Connected;                      // 11
        public static VoidFnOut3 IDE_GetConnectionInfo;         // 12
        public static IntFn IDE_GetWindowType;                  // 14
        public static IntFn IDE_GetWindowHandle;                // 16
        public static IntFn IDE_GetEditorHandle;                // 33
        public static VoidFnIntStrInt IDE_CreateWindow;         // 20
        public static IntFnIntStr IDE_OpenFile;                 // 21
        public static IntFn IDE_SaveFile;                       // 22
        public static StrFn IDE_Filename;                       // 23
        public static VoidFn IDE_CloseFile;                     // 24
        public static VoidFnStr IDE_SetFilename;                // 29
        public static IntFn IDE_GetWindowCount;                 // 80
        public static IntFnInt IDE_SelectWindow;                // 81
        public static IntFnInt IDE_ActivateWindow;              // 82
        public static IntFn IDE_GetReadOnly;                    // 26
        public static StrFn IDE_GetText;                        // 30
        public static StrFn IDE_GetSelectedText;                // 31
        public static IntFnStr IDE_SetText;                     // 34
        public static IntFnStr IDE_SetStatusMessage;            // 35
        // ---- SQL ----
        public static IntFnStr SQL_Execute;                     // 40
        public static IntFn SQL_FieldCount;                     // 41
        public static IntFn SQL_Eof;                            // 42
        public static IntFn SQL_Next;                           // 43
        public static StrFnInt SQL_Field;                       // 44
        public static StrFnInt SQL_FieldName;                   // 45
        public static StrFn SQL_ErrorMessage;                   // 48
        public static IntFnInt SQL_UsePlugInSession;            // 50
        public static VoidFnInt SQL_UseDefaultSession;          // 51
        public static StrFn SQL_GetDBMSGetOutput;               // 53
        // ---- IDE (objects / windows) ----
        public static VoidFnOut4 IDE_GetPopupObject;            // 74
        public static VoidFnStr3Int IDE_RefreshObject;          // 76
        public static StrFnStr3 IDE_GetObjectSource;            // 79
        public static IntFn IDE_WindowIsModified;               // 83
        public static IntFn IDE_WindowIsRunning;                // 84
        public static StrFn IDE_GetConnectAs;                   // 97
        public static IntFnOut4 IDE_GetWindowObject;            // 110
        public static IntFn IDE_GetCursorX;                     // 141
        public static IntFn IDE_GetCursorY;                     // 142
        public static VoidFnIntInt IDE_SetCursor;               // 143
        public static IntFnInt IDE_WindowHasEditor;             // 153
        public static IntFnStr4 IDE_ObjectAction;               // 165
        public static IntFnInt IDE_Perform;                     // 39
        public static StrFn IDE_TranslationLanguage;            // 131
        public static VoidFnIntInt IDE_KeyPress;                // 120
        public static IntFn IDE_ResultGridRowCount;             // 190
        public static IntFn IDE_ResultGridColCount;             // 191
        public static StrFnIntInt IDE_ResultGridCell;           // 192
        public static VoidFnOutIntOutStr IDE_GetCurrentStyle;   // 185
        public static StrFnStr1 IDE_GetGeneralPref;             // 218
        public static StrFnStr1 IDE_GetUserPref;                // 266
        public static IntFn IDE_GetWindowConnection;            // 245
        public static VoidFnIntStr4 IDE_ShowDifferences;        // 260
        // IDE_GetConnectionInfoEx (240) is deliberately not bound: it returns the password. IDE_GetConnectionInfo is
        // only called with the password parameter discarded.

        public static void Initialize(IntPtr table)
        {
            _table = table;
            SYS_Version = Get<IntFn>(1);
            SYS_RootDir = Get<StrFn>(3);
            IDE_Connected = Get<IntFn>(11);
            IDE_GetConnectionInfo = Get<VoidFnOut3>(12);
            IDE_GetWindowType = Get<IntFn>(14);
            IDE_GetWindowHandle = Get<IntFn>(16);
            IDE_GetEditorHandle = Get<IntFn>(33);
            IDE_CreateWindow = Get<VoidFnIntStrInt>(20);
            IDE_OpenFile = Get<IntFnIntStr>(21);
            IDE_SaveFile = Get<IntFn>(22);
            IDE_Filename = Get<StrFn>(23);
            IDE_CloseFile = Get<VoidFn>(24);
            IDE_SetFilename = Get<VoidFnStr>(29);
            IDE_GetWindowCount = Get<IntFn>(80);
            IDE_SelectWindow = Get<IntFnInt>(81);
            IDE_ActivateWindow = Get<IntFnInt>(82);
            IDE_GetReadOnly = Get<IntFn>(26);
            IDE_GetText = Get<StrFn>(30);
            IDE_GetSelectedText = Get<StrFn>(31);
            IDE_SetText = Get<IntFnStr>(34);
            IDE_SetStatusMessage = Get<IntFnStr>(35);
            SQL_Execute = Get<IntFnStr>(40);
            SQL_FieldCount = Get<IntFn>(41);
            SQL_Eof = Get<IntFn>(42);
            SQL_Next = Get<IntFn>(43);
            SQL_Field = Get<StrFnInt>(44);
            SQL_FieldName = Get<StrFnInt>(45);
            SQL_ErrorMessage = Get<StrFn>(48);
            SQL_UsePlugInSession = Get<IntFnInt>(50);
            SQL_UseDefaultSession = Get<VoidFnInt>(51);
            SQL_GetDBMSGetOutput = Get<StrFn>(53);
            IDE_GetPopupObject = Get<VoidFnOut4>(74);
            IDE_RefreshObject = Get<VoidFnStr3Int>(76);
            IDE_GetObjectSource = Get<StrFnStr3>(79);
            IDE_WindowIsModified = Get<IntFn>(83);
            IDE_WindowIsRunning = Get<IntFn>(84);
            IDE_GetConnectAs = Get<StrFn>(97);
            IDE_GetWindowObject = Get<IntFnOut4>(110);
            IDE_GetCursorX = Get<IntFn>(141);
            IDE_GetCursorY = Get<IntFn>(142);
            IDE_SetCursor = Get<VoidFnIntInt>(143);
            IDE_WindowHasEditor = Get<IntFnInt>(153);
            IDE_ObjectAction = Get<IntFnStr4>(165);
            IDE_Perform = Get<IntFnInt>(39);
            IDE_TranslationLanguage = Get<StrFn>(131);
            IDE_KeyPress = Get<VoidFnIntInt>(120);
            IDE_ResultGridRowCount = Get<IntFn>(190);
            IDE_ResultGridColCount = Get<IntFn>(191);
            IDE_ResultGridCell = Get<StrFnIntInt>(192);
            IDE_GetCurrentStyle = Get<VoidFnOutIntOutStr>(185);
            IDE_GetGeneralPref = Get<StrFnStr1>(218);
            IDE_GetUserPref = Get<StrFnStr1>(266);
            IDE_GetWindowConnection = Get<IntFn>(245);
            IDE_ShowDifferences = Get<VoidFnIntStr4>(260);
        }

        private static readonly System.Collections.Generic.List<int> Missing = new System.Collections.Generic.List<int>();

        private static T Get<T>(int index) where T : class
        {
            var addr = Marshal.ReadIntPtr(_table, index * IntPtr.Size);
            if (addr == IntPtr.Zero) { Missing.Add(index); return null; }
            return Marshal.GetDelegateForFunctionPointer<T>(addr);
        }

        /// <summary>For the log: IDE version and callbacks this PL/SQL Developer version does not provide.</summary>
        public static string Describe() =>
            $"PL/SQL Developer version {SYS_Version?.Invoke() ?? 0}, " +
            (Missing.Count == 0 ? "all callbacks available" : "missing callbacks: " + string.Join(", ", Missing));

        // ---- string helpers ----

        /// <summary>
        /// The Windows ANSI code page (1252 on German and English Windows). PL/SQL Developer applies
        /// CHARMODE=UTF8 only while it is itself calling into the plug-in; the agent calls the IDE
        /// asynchronously and those calls are always decoded as ANSI (verified with PL/SQL Developer 17).
        /// So the shim sets CHARMODE=ANSI and every string uses this code page, in every context.
        /// </summary>
        public static readonly Encoding Enc = Encoding.Default;

        /// <summary>Same code page without best-fit mapping: unrepresentable characters become '?'.</summary>
        private static readonly Encoding Strict =
            Encoding.GetEncoding(Encoding.Default.CodePage, new EncoderReplacementFallback("?"), new DecoderReplacementFallback("?"));

        /// <summary>Common symbols outside the ANSI code pages, replaced by plain equivalents.</summary>
        private static readonly (string From, string To)[] Transliterations =
        {
            ("→", "->"), ("←", "<-"), ("⇒", "=>"), ("⇐", "<="), ("↔", "<->"),
            ("≤", "<="), ("≥", ">="), ("≠", "<>"), ("≈", "~"),
            ("−", "-"), ("‐", "-"), ("‑", "-"), ("‒", "-"), ("―", "-"),
            (" ", " "), (" ", " "), (" ", " "), ("​", ""), ("﻿", ""),
        };

        /// <summary>Replaces common symbols the ANSI code page cannot represent (arrows, math signs, special spaces).</summary>
        public static string Transliterate(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? string.Empty;
            bool plain = true;
            foreach (var ch in s) if (ch > 'ÿ') { plain = false; break; }
            if (plain) return s;
            foreach (var (from, to) in Transliterations) if (s.IndexOf(from, StringComparison.Ordinal) >= 0) s = s.Replace(from, to);
            return s;
        }

        /// <summary>First character of <paramref name="s"/> that cannot pass the plug-in interface, or null.</summary>
        public static string FirstUnrepresentable(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            bool plain = true;
            foreach (var ch in s) if (ch > 'ÿ') { plain = false; break; }
            if (plain && Enc.CodePage == 1252) return null; // Latin-1 range: all in code page 1252 except a few control codes
            var back = Strict.GetString(Strict.GetBytes(s));
            int n = Math.Min(back.Length, s.Length);
            for (int i = 0; i < n; i++)
                if (back[i] != s[i])
                    return char.IsHighSurrogate(s[i]) && i + 1 < s.Length ? s.Substring(i, 2) : s[i].ToString();
            return back.Length != s.Length ? s.Substring(n, Math.Min(2, s.Length - n)) : null;
        }

        /// <summary>Copies a zero terminated string returned by the IDE (the IDE reuses its buffer).</summary>
        public static string Str(IntPtr p)
        {
            if (p == IntPtr.Zero) return string.Empty;
            int len = 0;
            while (Marshal.ReadByte(p, len) != 0) len++;
            if (len == 0) return string.Empty;
            var bytes = new byte[len];
            Marshal.Copy(p, bytes, 0, len);
            return Enc.GetString(bytes);
        }

        /// <summary>Zero terminated unmanaged copy of a string; free with <see cref="Free"/>.</summary>
        public static IntPtr Alloc(string s)
        {
            var bytes = Enc.GetBytes(Transliterate(s ?? string.Empty));
            var p = Marshal.AllocHGlobal(bytes.Length + 1);
            Marshal.Copy(bytes, 0, p, bytes.Length);
            Marshal.WriteByte(p, bytes.Length, 0);
            return p;
        }

        public static void Free(IntPtr p)
        {
            if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
        }

        /// <summary>using (var s = new NativeString("x")) Call(s.Ptr);</summary>
        public readonly struct NativeString : IDisposable
        {
            public readonly IntPtr Ptr;
            public NativeString(string s) { Ptr = Alloc(s); }
            public void Dispose() => Free(Ptr);
        }
    }

    internal static class Fpu
    {
        // Exported by the native shim (already loaded in the process under this name).
        [DllImport("PlsqlDevAgent.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint PdaMaskFpu();

        [DllImport("PlsqlDevAgent.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void PdaRestoreFpu(uint value);

        private static bool _available = true;

        /// <summary>Masks floating point exceptions (Delphi unmasks them) until disposed.</summary>
        public static Guard Mask()
        {
            if (!_available) return new Guard(null);
            try { return new Guard(PdaMaskFpu()); }
            catch { _available = false; return new Guard(null); }
        }

        public readonly struct Guard : IDisposable
        {
            private readonly uint? _old;
            public Guard(uint? old) { _old = old; }
            public void Dispose()
            {
                if (_old.HasValue) { try { PdaRestoreFpu(_old.Value); } catch { } }
            }
        }
    }
}
