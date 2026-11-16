// DreamWeavePages.cs - RynthDreamWeave's Bank page (2026-11-02): the every-minute bank check that gives
// Lum/hr its luminance on DreamWeave (DreamWeaveBank.cs), its setting and a Check now button. A tab of
// the DreamWeave window on RynthCore's bar (and of the /rserver window).
using System;
using System.Globalization;
using ImGuiNET;
using RynthCore.Plugin.ServerPack;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthDreamWeave;

/// <summary>The Bank page. Render thread: draws holding the bank's lock, and queues what it sends for the tick.</summary>
internal sealed class DreamWeavePages
{
    private readonly DreamWeaveBank _bank;
    private readonly Func<bool> _active;
    private readonly Action<Action> _runOnTick;
    private readonly Action _checkNow;
    private readonly Action _saveSettings;

    /// <param name="active">RynthDreamWeave's server is this world.</param>
    /// <param name="runOnTick">Queues work for the plugin tick (ServerPackPlugin.RunOnTick).</param>
    /// <param name="checkNow">Sends RynthDreamWeave's own /b (pump thread).</param>
    /// <param name="saveSettings">Writes the bank's settings (pump thread).</param>
    public DreamWeavePages(DreamWeaveBank bank, Func<bool> active, Action<Action> runOnTick, Action checkNow, Action saveSettings)
    {
        _bank = bank;
        _active = active;
        _runOnTick = runOnTick;
        _checkNow = checkNow;
        _saveSettings = saveSettings;
    }

    public ServerPage[] Pages() => new[]
    {
        new ServerPage("bank", "Bank", "DreamWeave's bank: the luminance Lum/hr counts (RynthDreamWeave).", DrawBank),
    };

    private void DrawBank()
    {
        if (!_active())
        {
            SafeImGui.TextWrapped("This page is for DreamWeave. RynthCore doesn't see this world as DreamWeave "
                                  + "(check RynthCore Options > Diagnostics, or /rserver info).");
            return;
        }
        lock (_bank.Gate)
        {
            SafeImGui.TextDisabled("Banked luminance");
            ImGui.SameLine(200f);
            SafeImGui.Text(_bank.Luminance is long lum ? lum.ToString("N0", CultureInfo.InvariantCulture) : "not read yet");
            if (_bank.ReadUtc != DateTime.MinValue)
            {
                ImGui.SameLine();
                SafeImGui.TextDisabled($"(read at {_bank.ReadUtc.ToLocalTime():HH:mm:ss})");
            }
            if (_bank.MovedIn > 0)
            {
                SafeImGui.TextDisabled("Deposited this login");
                ImGui.SameLine(200f);
                SafeImGui.Text(_bank.MovedIn.ToString("N0", CultureInfo.InvariantCulture));
                Tooltip("Luminance moved from the character into the bank: not counted as earned.");
            }

            ImGui.Spacing();
            bool check = _bank.CheckEveryMinute;
            if (ImGui.Checkbox("Check the bank every minute (Lum/hr)##dw_bank_check", ref check))
            {
                _bank.CheckEveryMinute = check;
                _runOnTick(_saveSettings);
            }
            Tooltip("DreamWeave keeps earned luminance in the bank, where the client can't see it. RynthDreamWeave "
                    + "sends /b once a minute and hides its reply; the Lum/hr in the Mini Remote and RynthTracker "
                    + "counts what the bank gains. A /b you type yourself always shows its reply.");

            if (ImGui.Button("Check now##dw_bank_now")) _runOnTick(_checkNow);
            Tooltip("Sends /b now.");
        }
    }

    private static void Tooltip(string text)
    {
        if (ImGui.IsItemHovered()) SafeImGui.SetTooltip(text);
    }
}
