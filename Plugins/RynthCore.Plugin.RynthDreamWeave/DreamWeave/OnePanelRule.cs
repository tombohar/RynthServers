// OnePanelRule.cs - one main panel at a time, as retail (2026-11-08, Tom: "in retail you can only have one
// panel open at a time (skills, inventory, etc.)"). Retail's main panels share one frame, so opening one puts
// the other away. The Character panel (SkillsPanel) is drawn by RynthDreamWeave, not AC, so this keeps the
// rule between it and AC's own main panels: it opening closes the AC panel that is open, and an AC panel
// opening closes it.
//
// Edges only: AC's open state is read at most 5 times a second, so a panel this rule just asked AC to close
// still reads open for a moment; that's not an AC panel opening, and an AC panel that stays open while the
// Character panel opens is the Character panel's open (it wins). Pure: the plugin feeds it what it sees each
// frame and does what it says (DreamWeaveSkills.ServerRender).
namespace RynthCore.Plugin.RynthDreamWeave;

internal sealed class OnePanelRule
{
    private bool _seen;
    private bool _oursWas;
    private uint _acWas;

    /// <summary>What to do this frame.</summary>
    public readonly record struct Step(uint CloseAc, bool CloseOurs);

    /// <summary>
    /// <paramref name="oursOpen"/>: the Character panel is open; <paramref name="acOpen"/>: bit i set = AC's
    /// main panel i is open (DreamWeaveSkills.MainAcPanels). The first call only learns where things are.
    /// </summary>
    public Step Next(bool oursOpen, uint acOpen)
    {
        if (!_seen)
        {
            _seen = true;
            _oursWas = oursOpen;
            _acWas = acOpen;
            return default;
        }
        uint opened = acOpen & ~_acWas;
        bool oursOpened = oursOpen && !_oursWas;
        _oursWas = oursOpen;
        _acWas = acOpen;
        // The Character panel just opened: it puts away the AC panels that are open.
        if (oursOpened) return new Step(acOpen, false);
        // An AC panel opened while it's open: it goes.
        // (_oursWas stays true: it still reads open on the next frame, and that isn't it opening again - which
        // would close the panel that just opened.)
        if (oursOpen && opened != 0) return new Step(0, true);
        return default;
    }

    /// <summary>Forget what was seen (the rule turned off, or the world changed).</summary>
    public void Reset() => _seen = false;
}
