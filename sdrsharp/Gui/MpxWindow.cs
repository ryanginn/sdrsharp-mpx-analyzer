using System;
using System.Drawing;
using System.Windows.Forms;

namespace SDRSharp.MPXAnalyzer.Gui
{
    // Floating window with the MPX display, for older SDR# builds that have no dock panels
    public sealed class MpxWindow : Form
    {
        readonly MpxCore core;
        readonly MpxDisplay display;
        bool closingForGood;

        internal MpxWindow(MpxCore core)
        {
            this.core = core;
            Text = "MPX Spectrum";
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            MinimumSize = new Size(480, 300);
            FormBorderStyle = FormBorderStyle.SizableToolWindow;

            display = new MpxDisplay(core) { Dock = DockStyle.Fill };
            Controls.Add(display);
            core.AddDisplay(display);
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
                core.OnWindowClosed();
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
