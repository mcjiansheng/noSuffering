using System;

namespace NoSuffering.Config;

public enum RefreshCostMode
{
    Free,
    Hp
}

public sealed record HostRules
{
    public bool EnableMapRollback { get; init; } = true;
    public bool EnableAncientReroll { get; init; } = true;
    public bool EnableAncientOptionsReroll { get; init; } = true;
    public bool EnableCombatRestart { get; init; } = true;
    public bool EnableCombatReroll { get; init; } = true;
    public bool EnableShopReroll { get; init; } = true;
    public bool EnableBossHealthIncrease { get; init; } = true;
    public RefreshCostMode AncientCostMode { get; init; } = RefreshCostMode.Free;
    public int AncientHpCost { get; init; } = 5;
    public RefreshCostMode OptionsCostMode { get; init; } = RefreshCostMode.Free;
    public int OptionsHpCost { get; init; } = 5;

    internal HostRules Normalize() => this with
    {
        AncientHpCost = Math.Clamp(AncientHpCost, 1, 99),
        OptionsHpCost = Math.Clamp(OptionsHpCost, 1, 99),
        AncientCostMode = Enum.IsDefined(AncientCostMode) ? AncientCostMode : RefreshCostMode.Free,
        OptionsCostMode = Enum.IsDefined(OptionsCostMode) ? OptionsCostMode : RefreshCostMode.Free
    };
}
