using System.Reflection;
using RoyalTerminal.Avalonia.Controls;
using RoyalTerminal.Terminal;
Console.WriteLine("Tessera dependency contract audit");
var wanted = new HashSet<string>{"TerminalControl", "TerminalTheme", "TerminalSettingsPanelState", "TerminalSettingsPanel", "TerminalSessionProfile", "TerminalSessionProfileDocument", "TerminalCaptureRuntime", "TerminalCaptureSession", "TerminalDataEventArgs", "TerminalSizeEventArgs", "RawTcpTransportOptions", "TelnetTransportOptions", "SerialTransportOptions", "SshTransportOptions", "TerminalSessionProfileMapper", "TerminalPasteSafetyDecision"};
var assemblies = new List<Assembly>{typeof(TerminalControl).Assembly, typeof(PtyTransportOptions).Assembly, Assembly.Load("RoyalTerminal.Avalonia.Settings")};
foreach(var assembly in assemblies)
foreach(var t in assembly.GetExportedTypes().Where(t=>wanted.Contains(t.Name)))
{
 Console.WriteLine("\nTYPE " + t.FullName);
 foreach(var c in t.GetConstructors()) Console.WriteLine("  CTOR " + c);
 foreach(var p in t.GetProperties(BindingFlags.Instance|BindingFlags.Public|BindingFlags.DeclaredOnly)) Console.WriteLine("  PROP " + p.PropertyType + " " + p.Name + " " + p.CanWrite);
 foreach(var e in t.GetEvents(BindingFlags.Instance|BindingFlags.Public|BindingFlags.DeclaredOnly)) Console.WriteLine("  EVENT " + e);
 foreach(var m in t.GetMethods(BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.DeclaredOnly).Where(m=>!m.IsSpecialName)) Console.WriteLine("  METHOD " + m);
}
