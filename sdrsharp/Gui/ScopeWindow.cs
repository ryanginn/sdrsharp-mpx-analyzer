using System;
using System.Drawing;
using System.Windows.Forms;

namespace SDRSharp.MPXAnalyzer.Gui
{
    // Floating window with the MPX scope, for older SDR# builds that have no dock panels (and don't load the
    // MPX Scope plugin unless Plugins.xml lists it)
    public sealed class ScopeWindow : Form
    {
        readonly MpxCore core;
        readonly ScopeDisplay display;
        bool closingForGood;

        internal ScopeWindow(MpxCore core)
        {
            this.core = core;
            Text = "MPX Scope";
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            MinimumSize = new Size(480, 240);
            FormBorderStyle = FormBorderStyle.SizableToolWindow;

            display = new ScopeDisplay(core) { Dock = DockStyle.Fill };
            Controls.Add(display);
            core.AddScope(display);
            ApplyTheme();
        }

        internal void ApplyTheme()
        {
            Theme.ApplyTo(this);
            BackColor = Theme.Background;
            display.ApplyTheme();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyTo(this);
        }

        // Closing by the user only hides the window, so it can be shown again from the plugin panel
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!closingForGood && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                core.OnScopeWindowClosed();
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        internal void CloseForGood()
        {
            closingForGood = true;
            Close();
        }
    }
}
