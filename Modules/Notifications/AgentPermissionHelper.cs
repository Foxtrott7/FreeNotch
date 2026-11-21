using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace WindowsNotch.Modules.Notifications
{
    /// <summary>
    /// Erkennt Berechtigungsanfragen von KI-Assistenten (AntiGravity, Claude, ChatGPT/Codex)
    /// und ermöglicht direktes Bestätigen oder Ablehnen über UI Automation / Fensterfokus.
    /// </summary>
    internal static class AgentPermissionHelper
    {
        private static readonly string[] AgentKeywords =
        {
            "antigravity", "claude", "codex", "chatgpt", "openai"
        };

        private static readonly string[] PermissionKeywords =
        {
            "permission", "approve", "approval", "allow", "confirm", "confirmation",
            "authorize", "accept", "run command", "execute", "waiting for", "action required",
            "berechtigung", "erlauben", "genehmigen", "bestätigen", "bestätigung",
            "zulassen", "freigeben", "ausführen", "aktion erforderlich"
        };

        private static readonly string[] ApproveButtonNames =
        {
            "Allow", "Approve", "Accept", "Yes", "OK", "Okay", "Run",
            "Zulassen", "Erlauben", "Genehmigen", "Bestätigen", "Ja", "Ausführen"
        };

        private static readonly string[] DenyButtonNames =
        {
            "Deny", "Reject", "Decline", "Cancel", "No",
            "Ablehnen", "Verweigern", "Abbrechen", "Nein"
        };

        public static bool IsPermissionRequest(string appName, string appId, string title, string body, out string agentKey)
        {
            agentKey = string.Empty;
            string combinedSource = $"{appName} {appId} {title} {body}";

            foreach (var agent in AgentKeywords)
            {
                if (combinedSource.Contains(agent, StringComparison.OrdinalIgnoreCase))
                {
                    agentKey = agent;
                    break;
                }
            }

            if (string.IsNullOrEmpty(agentKey))
                return false;

            string text = $"{title} {body}";
            return PermissionKeywords.Any(kw => text.Contains(kw, StringComparison.OrdinalIgnoreCase));
        }

        public static Task<bool> HandleDecisionAsync(string agentKey, bool approve)
        {
            return Task.Run(() =>
            {
                try
                {
                    IntPtr hwnd = FindAgentWindow(agentKey);
                    if (hwnd == IntPtr.Zero) return false;

                    if (approve)
                    {
                        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                        SetForegroundWindow(hwnd);
                    }

                    var root = AutomationElement.FromHandle(hwnd);
                    if (root == null) return false;

                    var targets = approve ? ApproveButtonNames : DenyButtonNames;
                    var buttons = root.FindAll(
                        TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

                    AutomationElement? bestMatch = null;
                    int bestScore = int.MaxValue;

                    foreach (AutomationElement btn in buttons)
                    {
                        string name = btn.Current.Name?.Trim() ?? string.Empty;
                        if (string.IsNullOrEmpty(name) || !btn.Current.IsEnabled) continue;

                        for (int i = 0; i < targets.Length; i++)
                        {
                            if (string.Equals(name, targets[i], StringComparison.OrdinalIgnoreCase))
                            {
                                if (i < bestScore)
                                {
                                    bestScore = i;
                                    bestMatch = btn;
                                }
                                break;
                            }
                            if (name.StartsWith(targets[i] + " ", StringComparison.OrdinalIgnoreCase) && i + 100 < bestScore)
                            {
                                bestScore = i + 100;
                                bestMatch = btn;
                            }
                        }
                    }

                    if (bestMatch != null && bestMatch.TryGetCurrentPattern(InvokePattern.Pattern, out object patternObj))
                    {
                        ((InvokePattern)patternObj).Invoke();
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    App.LogError(ex);
                }

                return false;
            });
        }

        private static IntPtr FindAgentWindow(string agentKey)
        {
            var searchTerms = agentKey switch
            {
                "codex" or "openai" or "chatgpt" => new[] { "chatgpt", "codex", "openai" },
                _ => new[] { agentKey }
            };

            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (proc.MainWindowHandle == IntPtr.Zero) continue;
                    string pName = proc.ProcessName ?? string.Empty;
                    string wTitle = proc.MainWindowTitle ?? string.Empty;

                    if (searchTerms.Any(t =>
                            pName.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                            wTitle.Contains(t, StringComparison.OrdinalIgnoreCase)))
                    {
                        return proc.MainWindowHandle;
                    }
                }
                catch
                {
                    // Prozess bereits beendet oder Zugriff verweigert
                }
                finally
                {
                    proc.Dispose();
                }
            }

            return IntPtr.Zero;
        }

        private const int SW_RESTORE = 9;

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);
    }
}
