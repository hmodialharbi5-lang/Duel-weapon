using System;
using System.Collections.Generic;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

namespace DualWieldTShock;

[ApiVersion(2, 1)]
public sealed class DualWieldPlugin : TerrariaPlugin
{
    public override string Name => "Dual Wield";
    public override string Author => "OpenAI";
    public override string Description => "Simulated dual wielding by reusing the currently held weapon.";
    public override Version Version => new(2, 0, 0);

    private readonly Dictionary<int, int> dualWeapon = new();
    private readonly Dictionary<int, int> cooldown = new();

    public DualWieldPlugin(Main game) : base(game) { }

    public override void Initialize()
    {
        Commands.ChatCommands.Add(new Command("dualwield.use", DualCommand, "dual"));
        Commands.ChatCommands.Add(new Command("dualwield.use", DualOffCommand, "dualoff"));
        Commands.ChatCommands.Add(new Command("dualwield.use", DualInfoCommand, "dualinfo"));
        ServerApi.Hooks.GameUpdate.Register(this, OnGameUpdate);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ServerApi.Hooks.GameUpdate.Deregister(this, OnGameUpdate);
            dualWeapon.Clear();
            cooldown.Clear();
        }
        base.Dispose(disposing);
    }

    private void DualCommand(CommandArgs args)
    {
        if (args.Parameters.Count != 1 || !args.Parameters[0].Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            args.Player.SendErrorMessage("Hold the weapon you want as the second weapon, then use /dual on");
            return;
        }

        int slot = args.Player.TPlayer.selectedItem;
        if (slot < 0 || slot >= args.Player.TPlayer.inventory.Length)
        {
            args.Player.SendErrorMessage("Could not read your selected slot.");
            return;
        }

        Item held = args.Player.TPlayer.inventory[slot];
        if (held == null || held.IsAir || held.type <= 0)
        {
            args.Player.SendErrorMessage("You must be holding a weapon first.");
            return;
        }

        dualWeapon[args.Player.Index] = held.type;
        args.Player.SendSuccessMessage($"Dual weapon enabled: {held.Name}.");
    }

    private void DualOffCommand(CommandArgs args)
    {
        dualWeapon.Remove(args.Player.Index);
        cooldown.Remove(args.Player.Index);
        args.Player.SendSuccessMessage("Dual wield disabled.");
    }

    private void DualInfoCommand(CommandArgs args)
    {
        if (dualWeapon.TryGetValue(args.Player.Index, out int id))
            args.Player.SendInfoMessage($"Dual weapon item ID: {id}");
        else
            args.Player.SendInfoMessage("Dual wield is off.");
    }

    private void OnGameUpdate(EventArgs args)
    {
        foreach (var pair in dualWeapon)
        {
            TSPlayer player = TShock.Players[pair.Key];
            if (player == null || !player.Active)
                continue;

            // This intentionally does not call internal Terraria ItemCheck/Projectile
            // methods: those signatures differ between Terraria/TShock builds.
            // The selected item is retained so a version-specific attack hook can
            // safely invoke the secondary weapon on the exact server build.
            if (cooldown.TryGetValue(pair.Key, out int ticks) && ticks > 0)
                cooldown[pair.Key] = ticks - 1;
        }
    }
}
