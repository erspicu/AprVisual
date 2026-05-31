using System;
using System.Drawing;
using System.Windows.Forms;
using AprVisual.Render;
using AprVisual.Rom;
using AprVisual.Sim;

namespace AprVisual
{
    /// <summary>
    /// S1 display window: a single Panel showing the live 256x240 switch-level frame, blitted
    /// via GDI SetDIBitsToDevice (Render.NativeGDI) from WireCore.FrameBuffer.
    ///
    /// Code-behind only (no .Designer.cs) — the layout is trivial.
    /// </summary>
    internal sealed class MainForm : Form
    {
        private const int DisplayScale = 3;   // integer display scale (256*3 x 240*3)

        private readonly Panel _screen;
        private string? _romPath;
        private NesRom? _rom;

        // S1: single-threaded — a WinForms timer kicks one "frame" of simulation then a blit.
        // (Later: move the sim to its own thread + double-buffer the framebuffer; see MD/note/03.)
        private readonly Timer _frameTimer;
        private bool _running;
        private bool _gdiReady;

        public MainForm(string? romPath = null)
        {
            _romPath = romPath;

            Text = "AprVisual — switch-level NES (S1)";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;

            _screen = new Panel
            {
                Location = new Point(8, 8),
                Size = new Size(WireCore.ScreenW * DisplayScale, WireCore.ScreenH * DisplayScale),
                BackColor = Color.Black,
            };
            Controls.Add(_screen);
            ClientSize = new Size(_screen.Right + 8, _screen.Bottom + 8);

            _frameTimer = new Timer { Interval = 16 };   // ~60 Hz; S1 doesn't lock the rate precisely
            _frameTimer.Tick += OnFrameTick;

            Load += OnLoad;
            FormClosed += OnClosed;
        }

        private void OnLoad(object? sender, EventArgs e)
        {
            if (_romPath is null) return;

            _rom = NesRom.LoadFromFile(_romPath);
            if (_rom is null)
            {
                MessageBox.Show(this, $"Failed to load ROM:\n{_romPath}", "AprVisual",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Text = $"AprVisual — {_rom.Name} (mapper {_rom.Mapper})";

            try
            {
                WireCore.LoadSystem(_rom);            // compose + attach handlers + copy ROM + power-on reset

                // GDI: blit WireCore.FrameBuffer straight onto the panel's HDC, scaled.
                unsafe { NativeGDI.Init(_screen.CreateGraphics(), WireCore.ScreenW, WireCore.ScreenH, WireCore.FrameBuffer, DisplayScale); }
                _gdiReady = true;

                _running = true;
                _frameTimer.Start();
            }
            catch (Exception ex)
            {
                // Keep the window up with a note rather than crashing.
                MessageBox.Show(this,
                    $"ROM loaded: {_rom.Name}\nPRG {_rom.PrgRom.Length / 1024} KB, CHR {_rom.ChrRom.Length / 1024} KB, mapper {_rom.Mapper}\n\n" +
                    $"Simulation failed to start:\n{ex.GetType().Name}: {ex.Message}",
                    "AprVisual", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnFrameTick(object? sender, EventArgs e)
        {
            if (!_running) return;
            try
            {
                // S1: the video handler is still a placeholder (black framebuffer), so step a fixed
                // chunk per tick and surface the live CPU state in the title bar rather than blocking
                // for a whole NES frame. (Real PPU vid_ → RGB decode comes later; then this becomes RunFrame.)
                WireCore.Step(50_000);               // ≈ 2000 6502 cycles per tick — keeps the UI responsive
                if (_gdiReady) NativeGDI.Present();
                Text = $"AprVisual — {_rom?.Name}  |  {WireCore.DumpCpuState()}";
            }
            catch (Exception ex)
            {
                _running = false;
                _frameTimer.Stop();
                Text = $"AprVisual — {_rom?.Name}  |  STOPPED: {ex.GetType().Name}: {ex.Message}";
            }
        }

        private void OnClosed(object? sender, FormClosedEventArgs e)
        {
            _frameTimer.Stop();
            if (_gdiReady) { NativeGDI.Free(); _gdiReady = false; }
            WireCore.Shutdown();
        }
    }
}
