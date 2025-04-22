﻿using BepInEx;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using System;
using System.Runtime.CompilerServices;
using System.Security.Permissions;
using UnityEngine;

// Allows access to private members
#pragma warning disable CS0618
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]
#pragma warning restore CS0618

namespace Catnap;

sealed class SleepData
{
    public int sleepDuration;
    public int groggy;
    public bool wasSleeping;
}

[BepInPlugin("com.dual.catnap", "Catnap", "1.0.2")]
sealed class Plugin : BaseUnityPlugin
{
    const int maxSpeedMultiplier = 10;
    const int startCurl = 60;
    const int startSleeping = 120;
    const int maxSleeping = startSleeping + 400;
    const int maxGroggy = 600;
    const float sleepVolumeMult = 0.25f;

    static readonly ConditionalWeakTable<Player, SleepData> sleepData = new();

    static SleepData Data(Player p) => sleepData.GetValue(p, _ => new());

    public void OnEnable()
    {
        new Hook(typeof(VirtualMicrophone).GetMethod("get_InWorldSoundsVolumeGoal"), GetterInWorldSoundsVolumeGoal);

        On.MainLoopProcess.RawUpdate += MainLoopProcess_RawUpdate;  // speed up update rate
        IL.MainLoopProcess.RawUpdate += MainLoopProcess_RawUpdateIL;// raise max game updates per frame
        On.HUD.HUD.Update += HUD_Update;                            // open hud while sleeping
        On.HUD.FoodMeter.Update += FoodMeter_Update;                // prevent food bar from moving when cat napping
        On.HUD.FoodMeter.Draw += FoodMeter_Draw;                    // prevent food bar from moving when cat napping
        On.Player.Stun += Player_Stun;                              // punish stuns while sleeping
        On.Player.checkInput += Player_checkInput;                  // prevent moving while sleeping
        On.Player.Update += Player_Update;                          // general update stuff
        On.Player.JollyEmoteUpdate += Player_JollyEmoteUpdate;      // override jolly sleep emote
    }

    private float GetterInWorldSoundsVolumeGoal(Func<VirtualMicrophone, float> orig, VirtualMicrophone self)
    {
        int duration = GlobalSleepDuration(self.room.game);
        if (duration != int.MaxValue) {
            return orig(self) * Mathf.Lerp(1, sleepVolumeMult, duration / (float)maxSleeping);
        }
        return orig(self);
    }

    private static int GlobalSleepDuration(RainWorldGame game)
    {
        int duration = int.MaxValue;

        foreach (var abstractPlayer in game.AlivePlayers) {
            // Get the minimum sleepDuration value. If not all players are sleeping, don't sleep.
            if (abstractPlayer?.realizedCreature is Player player && player.Consious) {
                if (duration > Data(player).sleepDuration)
                    duration = Data(player).sleepDuration;
            }
        }

        if (duration == int.MaxValue) return 0;

        return duration;
    }

    private void MainLoopProcess_RawUpdate(On.MainLoopProcess.orig_RawUpdate orig, MainLoopProcess self, float dt)
    {
        if (self is RainWorldGame game && game.IsStorySession && game.pauseMenu == null && game.processActive)
        {
            // run game up to N× faster while sleeping
            int sleepingAmount = GlobalSleepDuration(game);
            if (sleepingAmount > startSleeping)
                self.framesPerSecond = (int)RWCustom.Custom.LerpMap(sleepingAmount, startSleeping, maxSleeping, self.framesPerSecond, 40 * maxSpeedMultiplier);
        }

        orig(self, dt);
    }

    private void MainLoopProcess_RawUpdateIL(MonoMod.Cil.ILContext il)
    {
        ILCursor c = new ILCursor(il);
        try
        { // raise max game updates per frame depending on desired update rate
            c.GotoNext(
                i => i.MatchLdloc(0),
                i => i.MatchLdcI4(2)
                );
            c.GotoNext();
            c.Remove();
            c.Emit(OpCodes.Ldarg_0);
            c.EmitDelegate(GetMaxGameUpdatesPerFrame);
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to patch MainLoopProcess.RawUpdate");
            Logger.LogError(ex.Message);
        }
    }

    private static int GetMaxGameUpdatesPerFrame(MainLoopProcess mlp)
    {
        return Math.Max(2, (mlp.framesPerSecond + 19) / 40 - 1);
    }

    private void HUD_Update(On.HUD.HUD.orig_Update orig, HUD.HUD self)
    {
        orig(self);

        if (self.owner is Player player && Data(player).sleepDuration >= startSleeping) {
            self.showKarmaFoodRain = true;
        }
    }

    private void FoodMeter_Update(On.HUD.FoodMeter.orig_Update orig, HUD.FoodMeter self)
    {
        if (self.hud.owner is Player player && Data(player).sleepDuration >= 0) {
            int forceSleepCounter = player.forceSleepCounter;
            try {
                player.forceSleepCounter = 0;
                orig(self);
            }
            finally {
                player.forceSleepCounter = forceSleepCounter;
            }
        }
        else {
            orig(self);
        }
    }

    private void FoodMeter_Draw(On.HUD.FoodMeter.orig_Draw orig, HUD.FoodMeter self, float timeStacker)
    {
        if (self.hud.owner is Player player && Data(player).sleepDuration >= 0) {
            int forceSleepCounter = player.forceSleepCounter;
            try {
                player.forceSleepCounter = 0;
                orig(self, timeStacker);
            }
            finally {
                player.forceSleepCounter = forceSleepCounter;
            }
        }
        else {
            orig(self, timeStacker);
        }
    }

    private void Player_Stun(On.Player.orig_Stun orig, Player self, int st)
    {
        if (Data(self).sleepDuration >= startSleeping) {
            st += 60;
        }
        orig(self, st);
    }

    private void Player_checkInput(On.Player.orig_checkInput orig, Player self)
    {
        orig(self);

        if (Data(self).sleepDuration >= startCurl) {
            self.input[0] = new() {
                y = self.input[0].y,
                analogueDir = new(0, self.input[0].analogueDir.y)
            };
            if (self.input[0].y > 0)
                self.input[0].y = 0;
        }
    }

    private void Player_Update(On.Player.orig_Update orig, Player self, bool eu)
    {
        orig(self, eu);

        if (!self.abstractCreature.world.game.IsStorySession) {
            return;
        }

        var data = Data(self);

        SleepUpdate(self, data);
    }

    private static void SleepUpdate(Player self, SleepData data)
    {
        bool canSleep = self.abstractPhysicalObject.Room?.shelter == false && self.Consious && self.airInLungs >= 0.95f && self.Submersion <= 0.05f && self.grabbedBy.Count == 0 &&
            (self.bodyChunks[0].pos - self.bodyChunks[0].lastPos).sqrMagnitude < 4f &&
            (self.bodyChunks[1].pos - self.bodyChunks[1].lastPos).sqrMagnitude < 4f;

        if (canSleep && self.bodyMode == Player.BodyModeIndex.Crawl && self.animation == Player.AnimationIndex.None && !self.input[0].thrw && !self.input[0].pckp && self.input[0].x == 0 && self.input[0].y < 0) {
            data.sleepDuration++;
        }
        else {
            data.sleepDuration -= canSleep ? 2 : 10;
        }

        data.sleepDuration = Mathf.Clamp(data.sleepDuration, 0, maxSleeping);

        if (!canSleep) {
            if (data.wasSleeping) {
                data.wasSleeping = false;
                self.sleepCurlUp = 0;
                self.forceSleepCounter = 0;
            }
            return;
        }

        if (data.sleepDuration > startCurl) {
            self.sleepCurlUp = Mathf.Clamp01(3f * (data.sleepDuration - startCurl) / (maxSleeping - startCurl));
            data.wasSleeping = true;
        }

        if (data.sleepDuration >= startSleeping) {
            self.aerobicLevel *= 0.5f;
            data.groggy++;
        }
        else {
            data.groggy--;
        }

        data.groggy = Mathf.Clamp(data.groggy, 0, maxGroggy);

        if (data.sleepDuration >= startCurl) {
            self.Blink(5);
        }

        if (data.groggy > 0) {
            self.slowMovementStun = (int)RWCustom.Custom.LerpMap(data.groggy, 0, maxGroggy, 4, 10);

            if (data.sleepDuration > 0 || UnityEngine.Random.value < 0.18f) {
                self.Blink(12);
            }
        }
    }

    private void Player_JollyEmoteUpdate(On.Player.orig_JollyEmoteUpdate orig, Player self)
    {
        float sleepCurlUp = self.sleepCurlUp;
        try {
            self.emoteSleepCounter = 0;
            self.sleepCurlUp = 0;
            orig(self);
        }
        finally {
            self.emoteSleepCounter = 0;
            self.sleepCurlUp = sleepCurlUp;
        }
    }
}
