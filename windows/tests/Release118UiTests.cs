using ForeverScout.Core;

namespace ForeverScout.Tests;

/// <summary>
/// 1.0.18 i det riktiga fönstret (ScoutApp/MainForm, utan spelkatalog och utan nyckel – inget nätverk): bildraden med
/// länken Mina bilder, poängen som väntar på bekräftelse och beskedet överst (påminnelsen om /reload).
/// </summary>
public class Release118UiTests
{
    private static void WithApp(Action<ScoutApp> body)
    {
        Exception? err = null;
        var t = new Thread(() =>
        {
            try
            {
                Strings.Apply("sv");
                var settings = Settings.Load(TestEnv.AppData);
                settings.TrayHintShown = true; // ingen bubbla under testet
                Reset(settings);
                using var api = new ApiClient("http://127.0.0.1:9");
                using var app = new ScoutApp(settings, api);
                try { body(app); }
                finally
                {
                    app.Exit();
                    Reset(settings);
                    settings.Save();
                }
            }
            catch (Exception e) { err = e; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (err != null) throw new Xunit.Sdk.XunitException(err.ToString());
    }

    private static void Reset(Settings s)
    {
        s.Shots.Clear();
        s.PendingPoints = s.PendingFinds = s.MediaPublished = s.MediaPending = s.MediaRejected = null;
        s.AddonReloadVersion = null; s.AddonReloadSinceUtc = null;
        s.SilentSinceUtc = null; s.SilentDir = null; s.SilentWarnedUtc = null; s.SilentSaveUtc = null;
    }

    private static IEnumerable<Control> All(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var d in All(c)) yield return d;
        }
    }

    private static void Pump(int ms = 200)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(5); }
    }

    [Fact]
    public void WindowShowsPicturesLineWithLinkAndPendingPoints()
    {
        WithApp(app =>
        {
            app.Settings.MediaPublished = 3; app.Settings.MediaPending = 1; app.Settings.MediaRejected = 0;
            app.Settings.PendingPoints = 40; app.Settings.PendingFinds = 3;
            app.ShowWindow();
            Pump();
            var w = app.Window!;
            var texts = All(w).Where(c => c is Label).Select(c => c.Text).ToList();
            Assert.Contains("Bilder: 3 publicerade · 1 väntar på granskning · 0 avvisade", texts);
            Assert.Contains("+40 poäng väntar på att en spelare till bekräftar (3 fynd)", texts);
            Assert.Contains(Strings.S("mine.hint"), texts); // "Bekräftat = minst två spelare …" står kvar under raden
            var link = Assert.Single(All(w).OfType<LinkLabel>(), l => l.Text.StartsWith(Strings.S("pics.mine"), StringComparison.Ordinal));
            Assert.True(link.Visible);

            // Servern skickar inte fälten: bara det appen själv vet, och ingen rad om väntande poäng.
            app.Settings.MediaPublished = null; app.Settings.PendingPoints = null;
            app.Settings.Shots["a"] = new ShotState { Status = "queued" };
            w.ShowContribution();
            Pump(50);
            texts = All(w).Where(c => c is Label { Visible: true }).Select(c => c.Text).ToList();
            Assert.Contains("Bilder: 1 skickade", texts);
            Assert.DoesNotContain(texts, t => t.Contains("väntar på att en spelare", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void ReloadReminderIsShownAtTheTopUntilItNoLongerApplies()
    {
        WithApp(app =>
        {
            app.ShowWindow();
            Pump();
            var w = app.Window!;
            const string title = "Addonen uppdaterades till 1.0.26 – skriv /reload i spelet";
            Assert.DoesNotContain(All(w).OfType<Label>(), l => l.Text == title && l.Visible);

            Assert.True(ReloadReminder.OnInstalled(app.Settings, "1.0.25", "1.0.26", DateTime.UtcNow));
            app.WatchGame();
            Pump(50);
            Assert.Equal(title, app.Notice!.Title);
            var label = Assert.Single(All(w).OfType<Label>(), l => l.Text == title);
            Assert.True(label.Visible);
            Assert.True(label.Parent!.Parent!.Visible); // rutan överst i fönstret

            // Fönstret rivs i fältet och byggs upp igen: beskedet finns kvar.
            var (ask, action) = (app.Settings.DontAskOnClose, app.Settings.CloseAction);
            app.Settings.DontAskOnClose = true;
            app.Settings.CloseAction = "tray";
            w.Close();
            (app.Settings.DontAskOnClose, app.Settings.CloseAction) = (ask, action);
            Pump();
            app.ShowWindow();
            Pump();
            Assert.Contains(All(app.Window!).OfType<Label>(), l => l.Text == title && l.Visible);

            // En uppladdning från den nya addonversionen: beskedet tas bort.
            Assert.True(ReloadReminder.OnUploaded(app.Settings, "1.0.26"));
            app.WatchGame();
            Pump(50);
            Assert.Null(app.Notice);
            Assert.DoesNotContain(All(app.Window!).OfType<Label>(), l => l.Text == title && l.Visible);
        });
    }
}
