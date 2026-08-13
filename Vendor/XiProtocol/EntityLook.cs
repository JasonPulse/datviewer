namespace XiHeadless.Game;

/// <summary>
/// Minimal vendored copy of the one XiProtocol type the model resolver needs — an FFXI entity's
/// appearance (race/face/equipment model ids). The full XiProtocol wire/world-state stack is NOT
/// vendored; the DAT viewer only ever needs this POCO to describe "who to dress".
/// Source of truth: JasonPulse/vellichor → XiProtocol/vendor/Game/WorldState.cs (struct EntityLook).
/// </summary>
public struct EntityLook
{
    public ushort Type;      // MODELTYPE: 0 standard, 1 equipped, 2 door, 3 elevator, 4 ship, 6 automaton, 7 chocobo
    public ushort ModelId;   // STANDARD only: the mob/NPC model id
    public byte Face, Race;  // EQUIPPED
    public ushort Head, Body, Hands, Legs, Feet, Main, Sub, Ranged; // EQUIPPED slot model ids
    public bool Known;
}
