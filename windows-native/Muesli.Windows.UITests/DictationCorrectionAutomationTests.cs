using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows.Automation.Text;
using System.Windows.Forms;
using Microsoft.Data.Sqlite;

namespace Muesli.Windows.UITests;

[Collection("UiAutomation")]
public sealed class DictationCorrectionAutomationTests
{
    [WinUiAutomationFact]
    public void KeyboardSelectionTrimsSpacesAndSupportsEscapeAndEnter() => StaRunner.Run(() =>
    {
        using var session = MuesliWinUiSession.LaunchDictationCorrectionFixture("  Vatsan  ");
        session.Run(() =>
        {
            session.NavigateTo("dictations", "NavDictations", "DictationText");
            session.ActivateMainWindow();
            var text = session.RequireAutomationId("DictationText", true);
            text.SetFocus();
            ((TextPattern)text.GetCurrentPattern(TextPattern.Pattern)).DocumentRange.FindText("  ", false, false).Select();
            Thread.Sleep(600);
            Assert.Null(TryFind(session, "CorrectionReplacement"));
            SendKeys.SendWait("^a");
            Assert.Equal("Vatsan", Value(Find(session, "CorrectionReplacement")));
            SendKeys.SendWait("{ESC}");
            WaitUntil(() => TryFind(session, "CorrectionReplacement") is null);
            Thread.Sleep(500);
            Assert.Null(TryFind(session, "CorrectionReplacement"));
            Assert.Empty(DictionaryEntries(session));
            SendKeys.SendWait("{HOME}^a");
            SetReplacement(session, " Vathsan ");
            SetRemember(session, true);
            SendKeys.SendWait("{ENTER}");
            WaitUntil(() => Value(session.RequireAutomationId("DictationText")) == "  Vathsan  ");
            var entry = Assert.Single(DictionaryEntries(session));
            Assert.Equal("Vatsan", entry.GetProperty("phrase").GetString());
            Assert.Equal("Vathsan", entry.GetProperty("replacement").GetString());
        });
    });

    [WinUiAutomationFact]
    public void MouseSelectionAutomaticallyOpensCompactEditor() => StaRunner.Run(() =>
    {
        using var session = MuesliWinUiSession.LaunchDictationCorrectionFixture("Vatsan, meet Vatsan!");
        session.Run(() =>
        {
            session.NavigateTo("dictations", "NavDictations", "DictationText");
            session.ActivateMainWindow();
            var text = session.RequireAutomationId("DictationText", true);
            session.ActivateMainWindow();
            text.SetFocus();
            Thread.Sleep(300);
            var range = ((TextPattern)text.GetCurrentPattern(TextPattern.Pattern)).DocumentRange.FindText("Vatsan", false, false);
            var bounds = Assert.Single(range.GetBoundingRectangles());
            Assert.True(SetPhysicalCursorPos((int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2)));
            for (var click = 0; click < 2; click++)
            {
                mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
                mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(80);
            }
            AssertSelection(text, "Vatsan");
            Assert.Equal("Vatsan", Value(Find(session, "CorrectionReplacement")));
            SetReplacement(session, "Cancelled");
            var outside = session.RequireAutomationId("DictationsRefreshButton").Current.BoundingRectangle;
            Assert.True(SetPhysicalCursorPos((int)(outside.Left + outside.Width / 2), (int)(outside.Top + outside.Height / 2)));
            mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
            mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
            WaitUntil(() => TryFind(session, "CorrectionReplacement") is null);
            Assert.Equal("Vatsan, meet Vatsan!", Value(text));
            Assert.Empty(DictionaryEntries(session));
        });
    });

    [WinUiAutomationFact]
    public void SelectionAfterUnicodeAndLineBreaksKeepsExactOffsets() => StaRunner.Run(() =>
    {
        using var session = MuesliWinUiSession.LaunchDictationCorrectionFixture("  👩🏽‍💻 café\r\n  Vatsan  !  ");
        session.Run(() =>
        {
            session.NavigateTo("dictations", "NavDictations", "DictationText");
            var text = session.RequireAutomationId("DictationText", true);
            session.ActivateMainWindow();
            text.SetFocus();
            ((TextPattern)text.GetCurrentPattern(TextPattern.Pattern)).DocumentRange.FindText("  Vatsan  ", false, false).Select();
            AssertSelection(text, "  Vatsan  ");
            Assert.Equal("Vatsan", Value(Find(session, "CorrectionReplacement")));
            SetReplacement(session, "Vathsan");
            SendKeys.SendWait("{ENTER}");
            WaitUntil(() => Value(session.RequireAutomationId("DictationText")).Replace("\r\n", "\n").Replace('\r', '\n') == "  👩🏽‍💻 café\n  Vathsan  !  ");
            using var connection = OpenDatabase(session);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT text FROM dictations WHERE id = 'correction-ui';";
            Assert.Equal("  👩🏽‍💻 café\r\n  Vathsan  !  ", command.ExecuteScalar());
        });
    });

    [WinUiAutomationFact]
    public void SelectionCorrectionRestartAndDictionaryManagement() => StaRunner.Run(() =>
    {
        using var session = MuesliWinUiSession.LaunchDictationCorrectionFixture("Vatsan, meet Vatsan! 👩🏽‍💻");
        session.Run(() =>
        {
            session.NavigateTo("dictations", "NavDictations", "DictationText");
            var text = session.RequireAutomationId("DictationText", true);
            session.ActivateMainWindow();
            text.SetFocus();
            ((TextPattern)text.GetCurrentPattern(TextPattern.Pattern)).DocumentRange.FindText("Vatsan", false, false).Select();
            AssertSelection(text, "Vatsan");
            Assert.Equal("Vatsan", Value(Find(session, "CorrectionReplacement")));
            Assert.Equal("CorrectionReplacement", AutomationElement.FocusedElement.Current.AutomationId);
            // A correction changes only this dictation unless the user asks for it to be remembered.
            Assert.Equal(ToggleState.Off, Toggle(Find(session, "CorrectionRemember")).Current.ToggleState);
            SetReplacement(session, "Cancelled");
            SendKeys.SendWait("{ESC}");
            WaitUntil(() => TryFind(session, "CorrectionReplacement") is null);
            AssertSelection(text, "Vatsan");
            Assert.Empty(DictionaryEntries(session));
            Assert.Equal("Vatsan, meet Vatsan! 👩🏽‍💻", Value(text));
            Thread.Sleep(600);
            Assert.Null(TryFind(session, "CorrectionReplacement"));

            // The accessibility range identifies the second occurrence without searching in app code.
            var pattern = (TextPattern)text.GetCurrentPattern(TextPattern.Pattern);
            var range = pattern.DocumentRange.FindText("Vatsan", true, false);
            range.Select();
            AssertSelection(text, "Vatsan");
            Assert.Equal("Vatsan", Value(Find(session, "CorrectionReplacement")));
            SetReplacement(session, "Vathsan");
            SetRemember(session, true);
            var popover = Find(session, "CorrectionPopover");
            Assert.True(popover.Current.BoundingRectangle.Width / session.CurrentDpiScale < 350);
            Assert.True(popover.Current.BoundingRectangle.Height / session.CurrentDpiScale < 95);
            session.Capture("inline-correction-editor");
            SendKeys.SendWait("{ENTER}");
            WaitUntil(() => Value(session.RequireAutomationId("DictationText")) == "Vatsan, meet Vathsan! 👩🏽‍💻");
            Assert.Equal("Vathsan", Assert.Single(DictionaryEntries(session)).GetProperty("replacement").GetString());

            // The remaining occurrence is corrected once without changing the remembered entry.
            text = session.RequireAutomationId("DictationText");
            session.ActivateMainWindow();
            text.SetFocus();
            ((TextPattern)text.GetCurrentPattern(TextPattern.Pattern)).DocumentRange.FindText("Vatsan", false, false).Select();
            SetReplacement(session, "Guest");
            SetRemember(session, true);
            SendKeys.SendWait("{ENTER}");
            Assert.Contains("different remembered replacement", Find(session, "CorrectionFeedback").Current.Name);
            Assert.Equal(ToggleState.Off, Toggle(Find(session, "CorrectionReplaceConflict")).Current.ToggleState);
            Assert.Equal("Vatsan, meet Vathsan! 👩🏽‍💻", Value(text));
            SetRemember(session, false);
            SendKeys.SendWait("{ENTER}");
            WaitUntil(() => Value(session.RequireAutomationId("DictationText")) == "Guest, meet Vathsan! 👩🏽‍💻");
            Assert.Equal("Vathsan", Assert.Single(DictionaryEntries(session)).GetProperty("replacement").GetString());

            session.RestartPackaged();
            session.NavigateTo("dictations", "NavDictations", "DictationText");
            Assert.Equal("Guest, meet Vathsan! 👩🏽‍💻", Value(session.RequireAutomationId("DictationText")));
            session.NavigateTo("dictionary", "NavDictionary", "DictionaryEntryList");
            SetValue(FindName(session, "Dictionary replacement"), "Vathsan P");
            Invoke(FindName(session, "Save word"));
            WaitUntil(() => Assert.Single(DictionaryEntries(session)).GetProperty("replacement").GetString() == "Vathsan P");
            session.SelectAutomationId("DictionaryDeleteButton");
            Invoke(FindName(session, "Delete"));
            WaitUntil(() => DictionaryEntries(session).Length == 0);
            Assert.DoesNotContain("Unhandled UI exception", session.ReadLog());
            Assert.DoesNotContain("XamlParseException", session.ReadLog());
        });
    });

    private static JsonElement[] DictionaryEntries(MuesliWinUiSession session)
    {
        using var connection = OpenDatabase(session);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT phrase, replacement FROM dictionary_entries ORDER BY sort_order, id;";
        using var reader = command.ExecuteReader();
        var entries = new List<JsonElement>();
        while (reader.Read())
            entries.Add(JsonSerializer.SerializeToElement(new { phrase = reader.GetString(0), replacement = reader.GetString(1) }));
        return entries.ToArray();
    }

    private static SqliteConnection OpenDatabase(MuesliWinUiSession session)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(session.ProfileRoot, "data", "muesli.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void AssertSelection(AutomationElement element, string expected) =>
        WaitUntil(() => Assert.Single(((TextPattern)element.GetCurrentPattern(TextPattern.Pattern)).GetSelection()).GetText(-1) == expected);
    private static void SetRemember(MuesliWinUiSession session, bool remember)
    {
        var toggle = Toggle(Find(session, "CorrectionRemember"));
        if ((toggle.Current.ToggleState == ToggleState.On) != remember) toggle.Toggle();
        // Enter is handled by the editor, so keyboard focus goes back to the replacement box.
        Find(session, "CorrectionReplacement").SetFocus();
    }

    private static string Value(AutomationElement e) => ((ValuePattern)e.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
    private static void SetReplacement(MuesliWinUiSession session, string value)
    {
        var replacement = Find(session, "CorrectionReplacement");
        // Native flyout animation exposes the control before Opened transfers keyboard focus.
        WaitUntil(() => replacement.Current.HasKeyboardFocus);
        SendKeys.SendWait("^a");
        SendKeys.SendWait(value);
        WaitUntil(() => Value(replacement) == value);
    }
    private static void SetValue(AutomationElement e, string value) => ((ValuePattern)e.GetCurrentPattern(ValuePattern.Pattern)).SetValue(value);
    private static TogglePattern Toggle(AutomationElement e) => (TogglePattern)e.GetCurrentPattern(TogglePattern.Pattern);
    private static void Invoke(AutomationElement e) { ((InvokePattern)e.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); Thread.Sleep(200); }
    private static AutomationElement Find(MuesliWinUiSession s, string id) => Find(s, AutomationElement.AutomationIdProperty, id);
    private static AutomationElement FindName(MuesliWinUiSession s, string name) => Find(s, AutomationElement.NameProperty, name);
    private static AutomationElement Find(MuesliWinUiSession s, AutomationProperty property, string value)
    {
        AutomationElement? found = null;
        WaitUntil(() =>
        {
            foreach (AutomationElement window in AutomationElement.RootElement.FindAll(TreeScope.Children,
                         new PropertyCondition(AutomationElement.ProcessIdProperty, s.ProcessId)))
            {
                found = window.FindFirst(TreeScope.Descendants, new PropertyCondition(property, value));
                if (found is not null) return true;
            }
            return false;
        });
        return found!;
    }
    private static void WaitUntil(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (condition()) return;
            Thread.Sleep(100);
        }
        Assert.True(condition(), "The expected UI state did not appear.");
    }
    private static AutomationElement? TryFind(MuesliWinUiSession s, string id)
    {
        foreach (AutomationElement window in AutomationElement.RootElement.FindAll(TreeScope.Children,
                     new PropertyCondition(AutomationElement.ProcessIdProperty, s.ProcessId)))
        {
            var found = window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));
            if (found is not null) return found;
        }
        return null;
    }
    [DllImport("user32.dll")] private static extern bool SetPhysicalCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
}
