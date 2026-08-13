using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Xna.Framework;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

namespace DualWieldTShock;

[ApiVersion(2, 1)]
public sealed class DualWieldPlugin : TerrariaPlugin
{
    public override string Name => "Dual Wield";
    public override string Author => "OpenAI";
    public override string Description => "Server-side dual wielding for melee, magic and ranged projectile weapons.";
    public override Version Version => new(4, 0, 0);

    private readonly Dictionary<int, int> dualWeapon = new();
    private readonly Dictionary<int, int> lastAnimation = new();
    private readonly Dictionary<int, int> cooldown = new();

    // Resolved at runtime so the plugin does not depend on a particular
    // Terraria Player.ItemCheck overload.
    private MethodInfo? strikeNpcMethod;

    public DualWieldPlugin(Main game) : base(game) { }

    public override void Initialize()
    {
        strikeNpcMethod = FindStrikeNpcMethod();

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
            lastAnimation.Clear();
            cooldown.Clear();
            strikeNpcMethod = null;
        }

        base.Dispose(disposing);
    }

    private void DualCommand(CommandArgs args)
    {
        if (args.Parameters.Count != 1 ||
            !args.Parameters[0].Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            args.Player.SendErrorMessage(
                "Hold the weapon you want as the second weapon, then use /dual on.");
            return;
        }

        Player player = args.Player.TPlayer;
        int slot = player.selectedItem;

        if (slot < 0 || slot >= player.inventory.Length)
        {
            args.Player.SendErrorMessage("Could not read your selected slot.");
            return;
        }

        Item held = player.inventory[slot];

        if (held == null || held.IsAir || held.type <= 0 || held.damage <= 0)
        {
            args.Player.SendErrorMessage("You must be holding a weapon first.");
            return;
        }

        dualWeapon[args.Player.Index] = held.type;
        lastAnimation[args.Player.Index] = 0;
        cooldown[args.Player.Index] = 0;

        args.Player.SendSuccessMessage(
            $"Dual wield enabled: {held.Name}.");
    }

    private void DualOffCommand(CommandArgs args)
    {
        dualWeapon.Remove(args.Player.Index);
        lastAnimation.Remove(args.Player.Index);
        cooldown.Remove(args.Player.Index);

        args.Player.SendSuccessMessage("Dual wield disabled.");
    }

    private void DualInfoCommand(CommandArgs args)
    {
        if (dualWeapon.TryGetValue(args.Player.Index, out int type))
        {
            Item item = new Item();
            item.SetDefaults(type);
            args.Player.SendInfoMessage(
                $"Dual weapon: {item.Name} (ID {type}).");
        }
        else
        {
            args.Player.SendInfoMessage("Dual wield is off.");
        }
    }

    private void OnGameUpdate(EventArgs args)
    {
        // Copy the keys so a player can safely disconnect while this loop runs.
        int[] players = new int[dualWeapon.Count];
        dualWeapon.Keys.CopyTo(players, 0);

        foreach (int index in players)
        {
            if (!dualWeapon.TryGetValue(index, out int weaponType))
                continue;

            if (index < 0 || index >= TShock.Players.Length)
                continue;

            TSPlayer ts = TShock.Players[index];

            if (ts == null || !ts.Active || ts.Dead)
                continue;

            Player player = ts.TPlayer;

            if (cooldown.TryGetValue(index, out int cd) && cd > 0)
                cooldown[index] = cd - 1;

            int animation = player.itemAnimation;
            int previousAnimation =
                lastAnimation.TryGetValue(index, out int oldAnimation)
                    ? oldAnimation
                    : 0;

            lastAnimation[index] = animation;

            // Detect the start of the player's normal attack.
            if (animation <= 0 || previousAnimation > 0)
                continue;

            if (cooldown.TryGetValue(index, out cd) && cd > 0)
                continue;

            FireSecondary(ts, weaponType, index);
        }
    }

    private void FireSecondary(TSPlayer ts, int weaponType, int playerIndex)
    {
        Player player = ts.TPlayer;

        Item weapon = new Item();
        weapon.SetDefaults(weaponType);

        if (weapon.IsAir || weapon.damage <= 0)
            return;

        bool fired = false;

        try
        {
            // Magic/ranged/bow/gun/boomerang/etc. normally have a projectile.
            if (weapon.shoot > 0 && weapon.shootSpeed > 0f)
            {
                fired = FireProjectileWeapon(player, weapon);
            }
            else
            {
                // Pure melee weapons have no projectile. Handle their hit
                // server-side without calling Player.ItemCheck().
                fired = FireMeleeWeapon(player, weapon);
            }
        }
        catch (Exception ex)
        {
            ts.SendErrorMessage(
                $"Dual Wield attack error: {ex.InnerException?.Message ?? ex.Message}");
        }

        cooldown[playerIndex] = Math.Max(1, weapon.useTime);

        // Avoid spamming the player if a weapon simply isn't supported.
        if (!fired && weapon.shoot <= 0 && strikeNpcMethod == null)
        {
            ts.SendErrorMessage(
                "Dual Wield: this melee weapon could not be simulated on this Terraria build.");
        }
    }

    private bool FireProjectileWeapon(Player player, Item weapon)
    {
        Vector2 direction = GetAimDirection(player);

        Vector2 velocity = direction * weapon.shootSpeed;

        int damage = weapon.damage;
        float knockback = weapon.knockBack;

        int projectile = Projectile.NewProjectile(
            null,
            player.Center.X,
            player.Center.Y,
            velocity.X,
            velocity.Y,
            weapon.shoot,
            damage,
            knockback,
            player.whoAmI);

        if (projectile < 0 || projectile >= Main.maxProjectiles)
            return false;

        Projectile p = Main.projectile[projectile];
        p.originalDamage = damage;
        p.netUpdate = true;

        return true;
    }

    private bool FireMeleeWeapon(Player player, Item weapon)
    {
        if (strikeNpcMethod == null)
            return false;

        Vector2 direction = GetAimDirection(player);

        // Approximate a Terraria melee swing with a short front-facing arc.
        // This deliberately avoids ItemCheck so it remains independent of
        // the exact Terraria 1.4.5.6 ItemCheck signature.
        float range = Math.Max(48f, weapon.width + weapon.height + 24f);
        float halfArc = MathHelper.ToRadians(70f);

        bool hitSomething = false;

        for (int i = 0; i < Main.maxNPCs; i++)
        {
            NPC npc = Main.npc[i];

            if (npc == null || !npc.active || npc.friendly || npc.dontTakeDamage)
                continue;

            Vector2 toNpc = npc.Center - player.Center;
            float distance = toNpc.Length();

            if (distance > range || distance <= 0.01f)
                continue;

            toNpc.Normalize();

            float dot = Vector2.Dot(direction, toNpc);
            float angle = (float)Math.Acos(Math.Clamp(dot, -1f, 1f));

            if (angle > halfArc)
                continue;

            int hitDirection = npc.Center.X >= player.Center.X ? 1 : -1;
            int damage = weapon.damage;

            if (weapon.crit > 0 && Main.rand.Next(100) < weapon.crit)
                damage *= 2;

            if (InvokeStrikeNpc(
                npc,
                damage,
                weapon.knockBack,
                hitDirection))
            {
                hitSomething = true;
            }
        }

        return hitSomething;
    }

    private static Vector2 GetAimDirection(Player player)
    {
        Vector2 direction = new Vector2(player.direction, 0f);

        if (direction.LengthSquared() <= 0.0001f)
            direction = Vector2.UnitX;

        return Vector2.Normalize(direction);
    }

    private static MethodInfo? FindStrikeNpcMethod()
    {
        foreach (MethodInfo method in typeof(NPC).GetMethods(
                     BindingFlags.Instance |
                     BindingFlags.Public |
                     BindingFlags.NonPublic))
        {
            if (!method.Name.Equals("StrikeNPC", StringComparison.Ordinal))
                continue;

            ParameterInfo[] p = method.GetParameters();

            // Prefer the classic server-side damage signature:
            // StrikeNPC(int damage, float knockback, int hitDirection, ...)
            if (p.Length >= 3 &&
                p[0].ParameterType == typeof(int) &&
                p[1].ParameterType == typeof(float) &&
                p[2].ParameterType == typeof(int))
            {
                return method;
            }
        }

        return null;
    }

    private bool InvokeStrikeNpc(
        NPC npc,
        int damage,
        float knockback,
        int hitDirection)
    {
        if (strikeNpcMethod == null)
            return false;

        ParameterInfo[] parameters = strikeNpcMethod.GetParameters();
        object?[] values = new object?[parameters.Length];

        for (int i = 0; i < parameters.Length; i++)
        {
            Type type = parameters[i].ParameterType;

            if (i == 0 && type == typeof(int))
                values[i] = damage;
            else if (i == 1 && type == typeof(float))
                values[i] = knockback;
            else if (i == 2 && type == typeof(int))
                values[i] = hitDirection;
            else if (type == typeof(bool))
                values[i] = false;
            else if (type == typeof(float))
                values[i] = 0f;
            else if (type == typeof(int))
                values[i] = 0;
            else if (type == typeof(double))
                values[i] = 0d;
            else if (type == typeof(bool?))
                values[i] = null;
            else
                values[i] = type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        strikeNpcMethod.Invoke(npc, values);
        return true;
    }
}
