using System;
using System.Collections.Generic;
using System.Reflection;
using DaggerfallConnect;
using NUnit.Framework;
using UnityEngine;

using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Guilds;
using DaggerfallWorkshop.Game.Items;
using DaggerfallWorkshop.Game.Serialization;
using DaggerfallWorkshop.Utility;
using Object = UnityEngine.Object;

#region Test doubles & installers

// Minimal IGuild implementation used only to control AvoidDeath()
public class TestGuildAvoidDeath : IGuild
{
    public bool NextAvoidDeath;

    // --- Core behavior for tests ---
    public int Calls { get; private set; }
    public bool AvoidDeath()
    {
        Calls++;
        return NextAvoidDeath;
    }

    // --- Properties (stubbed) ---
    public List<DFCareer.Skills> GuildSkills => null;
    public int Rank { get; set; }
    public string[] RankTitles => null;
    public List<DFCareer.Skills> TrainingSkills => null;

    // --- Membership / lifecycle (stubbed) ---
    public bool IsMember() => true;
    public bool IsEligibleToJoin(PlayerEntity playerEntity) => true;
    public void Join() { }
    public void Leave() { }

    // --- Titles / tokens (stubbed) ---
    public string GetTitle() => "Member";
    public string GetGuildName() => "TestGuild";
    public TextFile.Token[] TokensWelcome() => Array.Empty<TextFile.Token>();
    public TextFile.Token[] TokensExpulsion() => Array.Empty<TextFile.Token>();
    public TextFile.Token[] TokensDemotion() => Array.Empty<TextFile.Token>();
    public TextFile.Token[] TokensEligible(PlayerEntity playerEntity) => Array.Empty<TextFile.Token>();
    public TextFile.Token[] TokensIneligible(PlayerEntity playerEntity) => Array.Empty<TextFile.Token>();
    public TextFile.Token[] TokensPromotion(int newRank) => Array.Empty<TextFile.Token>();

    // --- Rank / reputation (stubbed) ---
    public int GetReputation(PlayerEntity playerEntity) => 0;
    public TextFile.Token[] UpdateRank(PlayerEntity playerEntity) => Array.Empty<TextFile.Token>();
    public void ImportLastRankChange(uint timeOfLastRankChange) { }

    // --- Data persistence (stubbed) ---
    public GuildMembership_v1 GetGuildData() => new GuildMembership_v1();
    public void RestoreGuildData(GuildMembership_v1 data) { }

    // --- Faction / identity (stubbed) ---
    public string GetAffiliation() => "Test";
    public int GetFactionId() => 0;

    // --- Services & prices (stubbed) ---
    public bool CanAccessService(GuildServices service) => false;
    public bool CanAccessLibrary() => false;
    public bool CanRest() => false;
    public int GetTrainingPrice() => 0;
    public int GetTrainingMax(DFCareer.Skills skill) => 0;
    public int ReducedRepairCost(int price) => price;
    public int ReducedIdentifyCost(int price) => price;
    public int ReducedCureCost(int price) => price;
    public int AlterReward(int reward) => reward;

    // --- Travel / utility perks (stubbed) ---
    public bool FreeShipTravel() => false;
    public bool FreeHealing() => false;
    public bool FreeMagickaRecharge() => false;
    public bool FreeTavernRooms() => false;
    public int FastTravel(int duration) => duration;
    public int DeepBreath(int duration) => duration;
    public bool HallAccessAnytime() => false;

    // --- Quest-related helpers (stubbed) ---
    public bool IsSatisfyQuestReqByLevel() => true;

    // --- Macro provider (stubbed) ---
    public MacroDataSource GetMacroDataSource() => null;
}

// Installs a controllable IGuild into real GuildManager.memberships via reflection
public static class GuildManagerTestInstaller
{
    // Injects a fake guild into the private 'memberships' dictionary
    public static TestGuildAvoidDeath InstallAvoidDeathGuild(GuildManager realGuildManager, bool avoidDeath)
    {
        var t = typeof(GuildManager);
        var membershipsField = t.GetField("memberships", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(membershipsField, "GuildManager.memberships field not found.");

        var dict = membershipsField.GetValue(realGuildManager) as Dictionary<FactionFile.GuildGroups, IGuild>;
        if (dict == null)
        {
            dict = new Dictionary<FactionFile.GuildGroups, IGuild>();
            membershipsField.SetValue(realGuildManager, dict);
        }

        var fake = new TestGuildAvoidDeath { NextAvoidDeath = avoidDeath };
        dict[FactionFile.GuildGroups.FightersGuild] = fake;

        return fake;
    }
}

#endregion

#region Testable PlayerEntity

// Testable subclass to intercept RaiseOnDeathEvent and to set internal state safely
public class TestablePlayerEntity : PlayerEntity
{
    public Object gameObject;
    public bool DeathRaised { get; private set; }

    public TestablePlayerEntity(DaggerfallEntityBehaviour behaviour, bool deathRaised) : base(behaviour)
    {
        DeathRaised = deathRaised;
    }

    // Intercept death event
    protected override void RaiseOnDeathEvent()
    {
        DeathRaised = true;
        base.RaiseOnDeathEvent();
    }

    // Set state using resilient reflection (property first, then field)
    public void SetState(int current, int _ignored, bool god)
    {
        // current health can be a property or a field
        SetMember(typeof(PlayerEntity), this, "CurrentHealth", current);
        SetMember(typeof(PlayerEntity), this, "currentHealth", current);

        // godMode may not exist on PlayerEntity; try both casing variants and ignore if absent
        TrySetMember(typeof(PlayerEntity), this, "godMode", god);
        TrySetMember(typeof(PlayerEntity), this, "GodMode", god);

        DeathRaised = false;
    }

    // Returns current health from either property or field
    public int GetCurrent()
    {
        if (TryGetMember(typeof(PlayerEntity), this, "CurrentHealth", out int vProp))
        {
            return vProp;
        }

        if (TryGetMember(typeof(PlayerEntity), this, "currentHealth", out int vField))
        {
            return vField;
        }

        Assert.Fail("Neither 'CurrentHealth' property nor 'currentHealth' field found on PlayerEntity.");
        return 0;
    }

    // --- God mode toggler (best-effort) ---
    public bool TrySetGodMode(bool value)
    {
        return TrySetMember(typeof(PlayerEntity), this, "godMode", value)
            || TrySetMember(typeof(PlayerEntity), this, "GodMode", value);
    }

    // --- Fatigue helpers ---
    public int GetCurrentFatigue()
    {
        if (TryGetMember(typeof(PlayerEntity), this, "CurrentFatigue", out int vProp))
        {
            return vProp;
        }

        if (TryGetMember(typeof(PlayerEntity), this, "currentFatigue", out int vField))
        {
            return vField;
        }
        Assert.Fail("Neither 'CurrentFatigue' property nor 'currentFatigue' field found on PlayerEntity.");
        return 0;
    }
    public int GetMaxFatigue()
    {
        if (TryGetMember(typeof(PlayerEntity), this, "MaxFatigue", out int vProp))
        {
            return vProp;
        }

        if (TryGetMember(typeof(PlayerEntity), this, "maxFatigue", out int vField))
        {
            return vField;
        }
        var p = typeof(PlayerEntity).GetProperty("MaxFatigue",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
        if (p != null && p.PropertyType == typeof(int))
        {
            return (int)p.GetValue(this);
        }
        Assert.Fail("MaxFatigue member not found on PlayerEntity.");
        return 0;
    }

    // --- Magicka helpers ---
    public int GetCurrentMagicka()
    {
        if (TryGetMember(typeof(PlayerEntity), this, "CurrentMagicka", out int vProp))
        {
            return vProp;
        }

        if (TryGetMember(typeof(PlayerEntity), this, "currentMagicka", out int vField))
        {
            return vField;
        }
        Assert.Fail("Neither 'CurrentMagicka' property nor 'currentMagicka' field found on PlayerEntity.");
        return 0;
    }
    public int GetMaxMagicka()
    {
        if (TryGetMember(typeof(PlayerEntity), this, "MaxMagicka", out int vProp))
        {
            return vProp;
        }

        if (TryGetMember(typeof(PlayerEntity), this, "maxMagicka", out int vField))
        {
            return vField;
        }
        var p = typeof(PlayerEntity).GetProperty("MaxMagicka",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
        if (p != null && p.PropertyType == typeof(int))
        {
            return (int)p.GetValue(this);
        }
        Assert.Fail("MaxMagicka member not found on PlayerEntity.");
        return 0;
    }


    public bool TrySetMaxHealth(int value)
    {
        // property on PlayerEntity
        var p = typeof(PlayerEntity).GetProperty("MaxHealth",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
        if (p != null && p.CanWrite && p.PropertyType == typeof(int))
        {
            p.SetValue(this, value); return true;
        }

        // field on PlayerEntity
        var f = typeof(PlayerEntity).GetField("maxHealth",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
        if (f != null && f.FieldType == typeof(int))
        {
            f.SetValue(this, value);
            return true;
        }

        // try base type (often DaggerfallEntity)
        var bt = typeof(PlayerEntity).BaseType;
        if (bt != null)
        {
            var bp = bt.GetProperty("MaxHealth",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);

            if (bp != null && bp.CanWrite && bp.PropertyType == typeof(int))
            {
                bp.SetValue(this, value);
                return true;
            }

            var bf = bt.GetField("maxHealth",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
            if (bf != null && bf.FieldType == typeof(int))
            {
                bf.SetValue(this, value);
                return true;
            }
        }

        return false; // not settable on this DFU build
    }

    // Returns MaxHealth if available (property or field)
    public int GetMax()
    {
        if (TryGetMember(typeof(PlayerEntity), this, "MaxHealth", out int vProp))
        {
            return vProp;
        }

        if (TryGetMember(typeof(PlayerEntity), this, "maxHealth", out int vField))
        {
            return vField;
        }

        var p = typeof(PlayerEntity).GetProperty("MaxHealth",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
        if (p != null && p.PropertyType == typeof(int))
        {
            return (int)p.GetValue(this);
        }

        Assert.Fail("MaxHealth member not found on PlayerEntity.");
        return 0;
    }

    // --- reflection helpers ---

    // Tries property then field; does nothing if not found
    private static void SetMember(Type t, object obj, string name, object value)
    {
        var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (p != null && p.CanWrite)
        {
            p.SetValue(obj, value);
            return;
        }
        var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (f != null)
        {
            f.SetValue(obj, value);
        }
    }

    // Best-effort setter; returns false if member is absent
    private static bool TrySetMember(Type t, object obj, string name, object value)
    {
        var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (p != null && p.CanWrite)
        {
            p.SetValue(obj, value);
            return true;
        }
        var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (f != null)
        {
            f.SetValue(obj, value);
            return true;
        }
        return false;
    }

    // Generic getter for int members (property or field)
    private static bool TryGetMember(Type t, object obj, string name, out int value)
    {
        var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (p != null && p.PropertyType == typeof(int))
        {
            value = (int)p.GetValue(obj);
            return true;
        }

        var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (f != null && f.FieldType == typeof(int))
        {
            value = (int)f.GetValue(obj);
            return true;
        }

        value = 0;
        return false;
    }

    // --- skills helpers ---
    public void InitSkillUses(int size = 63)
    {
        // Creates or replaces the 'skillUses' short[] with the given size
        var f = typeof(PlayerEntity).GetField("skillUses", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.IsNotNull(f, "Field 'skillUses' not found on PlayerEntity.");
        var arr = new short[size];
        f.SetValue(this, arr);
    }

    public void SetSkillUseAt(int idx, short val)
    {
        var f = typeof(PlayerEntity).GetField("skillUses", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        var arr = (short[])f.GetValue(this);
        Assert.IsNotNull(arr, "skillUses is null.");
        Assert.GreaterOrEqual(idx, 0);
        Assert.Less(idx, arr.Length);
        arr[idx] = val;
    }

    public short GetSkillUseAt(int idx)
    {
        var f = typeof(PlayerEntity).GetField("skillUses", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        var arr = (short[])f.GetValue(this);
        Assert.IsNotNull(arr, "skillUses is null.");
        return arr[idx];
    }

    public void SetSkillUsesNull()
    {
        var f = typeof(PlayerEntity).GetField("skillUses", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.IsNotNull(f, "Field 'skillUses' not found on PlayerEntity.");
        f.SetValue(this, null);
    }

    // --- thieves/DB fields helpers ---
    public void SetThievesTally(byte value)
    {
        SetPrivateByte("thievesGuildRequirementTally", value);
    }

    public void SetDarkTally(byte value)
    {
        SetPrivateByte("darkBrotherhoodRequirementTally", value);
    }

    public uint GetThievesLetterTime()
    {
        return GetPrivateUInt("timeForThievesGuildLetter");
    }

    public uint GetDarkLetterTime()
    {
        return GetPrivateUInt("timeForDarkBrotherhoodLetter");
    }

    private void SetPrivateByte(string name, byte v)
    {
        var f = typeof(PlayerEntity).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.IsNotNull(f, $"Field '{name}' not found.");
        f.SetValue(this, v);
    }

    private uint GetPrivateUInt(string name)
    {
        var f = typeof(PlayerEntity).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.IsNotNull(f, $"Field '{name}' not found.");
        return (uint)f.GetValue(this);
    }
}

#endregion

public class SetPlayerEntityTests
{
    private TestablePlayerEntity player;
    private GameObject gmGo;
    private GameObject behGo;

    [SetUp]
    public void Setup()
    {
        // Ensure GameManager.Instance exists (MonoBehaviour singleton)
        var gmExisting = GameManager.Instance;
        if (gmExisting == null)
        {
            gmGo = new GameObject("GameManager_Test");
            gmExisting = gmGo.AddComponent<GameManager>();
        }

        // Ensure DaggerfallUnity.Instance with WorldTime exists
        if (DaggerfallUnity.Instance == null)
        {
            var dfuGo = new GameObject("DFU_Test");
            dfuGo.AddComponent<DaggerfallUnity>();
        }

        // Attach WorldTime component if missing
        if (DaggerfallUnity.Instance != null && DaggerfallUnity.Instance.WorldTime == null)
        {
            DaggerfallUnity.Instance.gameObject.AddComponent<WorldTime>();
        }

        // Initialize to some known time value
        if (DaggerfallUnity.Instance != null)
        {
            if (DaggerfallUnity.Instance.WorldTime != null)
            {
                var now = DaggerfallUnity.Instance.WorldTime.DaggerfallDateTime.ToClassicDaggerfallTime();
                UnityEngine.Debug.Log($"[Test Setup] DFU WorldTime initialized at {now} classic minutes.");
            }
        }

        // Ensure GuildManager exists
        if (gmExisting.GuildManager == null)
        {
            var guildMgr = new GuildManager();
            var prop = typeof(GameManager).GetProperty("GuildManager", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.IsNotNull(prop, "GameManager.GuildManager property not found.");
            if (prop != null)
            {
                prop.SetValue(gmExisting, guildMgr);
            }
        }

        // Default: AvoidDeath() = false (tests override as needed)
        GuildManagerTestInstaller.InstallAvoidDeathGuild(GameManager.Instance.GuildManager, avoidDeath: false);

        // Create PlayerEntity with a dummy DaggerfallEntityBehaviour
        behGo = new GameObject("PlayerBehaviour_Test");
        var dummyBehaviour = behGo.AddComponent<DaggerfallEntityBehaviour>();
        player = new TestablePlayerEntity(dummyBehaviour, false);

        // Initialize state
        player.SetState(current: 50, _ignored: 0, god: false);
    }

    [TearDown]
    public void Teardown()
    {
        if (gmGo)
        {
            UnityEngine.Object.DestroyImmediate(gmGo);
        }

        if (behGo)
        {
            UnityEngine.Object.DestroyImmediate(behGo);
        }

        var dfuGo = GameObject.Find("DFU_Test");
        if (dfuGo)
        {
            UnityEngine.Object.DestroyImmediate(dfuGo);
        }

        gmGo = null;
        behGo = null;
    }

    [Test]
    public void GodMode_AlwaysSetsAndReturns_MaxHealth()
    {
        // Check that godMode member exists; otherwise skip this case for this DFU build
        bool hasGodMode =
            typeof(PlayerEntity).GetField("godMode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null ||
            typeof(PlayerEntity).GetProperty("godMode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null ||
            typeof(PlayerEntity).GetProperty("GodMode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null;

        if (!hasGodMode)
        {
            Assert.Ignore("PlayerEntity has no 'godMode' member; skipping GodMode test.");
        }

        player.SetState(current: 1, _ignored: 0, god: true);

        int ret = player.SetHealth(amount: 5, restoreMode: false);

        int max = player.GetMax();
        Assert.AreEqual(max, ret, "Expected return to be MaxHealth in god mode.");
        Assert.AreEqual(max, player.GetCurrent(), "Expected current health to be MaxHealth in god mode.");
    }

    [Test]
    public void RestoreMode_SetsRawAmount_WithoutClamp()
    {
        player.SetState(current: 10, _ignored: 0, god: false);

        int ret = player.SetHealth(amount: 150, restoreMode: true);

        Assert.AreEqual(150, ret, "restoreMode should bypass clamping and set raw amount.");
        Assert.AreEqual(150, player.GetCurrent(), "restoreMode should set currentHealth to raw amount.");
        Assert.IsFalse(player.DeathRaised, "Death event should not be raised when health > 0.");
    }

    [Test]
    public void NonRestore_Clamps_ToZeroAndMax()
    {
        player.SetState(current: 10, _ignored: 0, god: false);

        int retHigh = player.SetHealth(amount: 999999, restoreMode: false);
        int max = player.GetMax();
        Assert.AreEqual(max, retHigh, "Health should be clamped to MaxHealth.");
        Assert.AreEqual(max, player.GetCurrent(), "currentHealth should be clamped to MaxHealth.");

        int retLow = player.SetHealth(amount: -50, restoreMode: false);
        Assert.AreEqual(0, retLow, "Health should be clamped to zero.");
        Assert.AreEqual(0, player.GetCurrent(), "currentHealth should be clamped to zero.");
    }

    [Test]
    public void Death_Avoided_ByGuild_SetsToTenPercent_AndNoDeathEvent()
    {
        player.SetState(current: 10, _ignored: 0, god: false);
        GuildManagerTestInstaller.InstallAvoidDeathGuild(GameManager.Instance.GuildManager, avoidDeath: true);

        int ret = player.SetHealth(amount: -999, restoreMode: false);

        int expected = (int)(player.GetMax() * 0.1f);
        Assert.AreEqual(expected, ret, "AvoidDeath should set health to 10% of MaxHealth.");
        Assert.AreEqual(expected, player.GetCurrent(), "currentHealth should be 10% of MaxHealth after AvoidDeath.");
        Assert.IsFalse(player.DeathRaised, "Death event must NOT be raised when AvoidDeath succeeds.");
    }

    [Test]
    public void Death_NotAvoided_RaisesDeathEvent_AndRemainsAtZeroOrBelow()
    {
        player.SetState(current: 10, _ignored: 0, god: false);
        GuildManagerTestInstaller.InstallAvoidDeathGuild(GameManager.Instance.GuildManager, avoidDeath: false);

        int ret = player.SetHealth(amount: -999, restoreMode: false);

        Assert.LessOrEqual(ret, 0, "When death is not avoided, SetHealth should return zero or below.");
        Assert.IsTrue(player.DeathRaised, "Death event should be raised when AvoidDeath fails.");
    }

    [Test]
    public void PositiveResult_NoDeath()
    {
        // Arrange
        player.SetState(current: 10, _ignored: 0, god: false);
        GuildManagerTestInstaller.InstallAvoidDeathGuild(GameManager.Instance.GuildManager, avoidDeath: false);

        // Act (use restoreMode: true to bypass MaxHealth clamp)
        int ret = player.SetHealth(amount: 50, restoreMode: true);

        // Assert
        Assert.AreEqual(50, ret, "SetHealth should return the assigned health.");
        Assert.AreEqual(50, player.GetCurrent(), "currentHealth should match the assigned health.");
        Assert.IsFalse(player.DeathRaised, "Death event should not be raised for positive health.");
    }

    [Test]
    public void Zero_NoAvoid_RaisesDeathEvent()
    {
        player.SetState(current: 10, _ignored: 0, god: false);
        GuildManagerTestInstaller.InstallAvoidDeathGuild(GameManager.Instance.GuildManager, avoidDeath: false);

        int ret = player.SetHealth(amount: 0, restoreMode: false);

        Assert.AreEqual(0, ret, "Zero health should remain zero when death not avoided.");
        Assert.IsTrue(player.DeathRaised, "Death event should be raised when health is zero and not avoided.");
    }

    [Test]
    public void Zero_Avoid_SetsTenPercent()
    {
        player.SetState(current: 10, _ignored: 0, god: false);
        GuildManagerTestInstaller.InstallAvoidDeathGuild(GameManager.Instance.GuildManager, avoidDeath: true);

        int ret = player.SetHealth(amount: 0, restoreMode: false);

        int expected = (int)(player.GetMax() * 0.1f);
        Assert.AreEqual(expected, ret);
        Assert.AreEqual(expected, player.GetCurrent());
        Assert.IsFalse(player.DeathRaised);
    }

    [Test]
    public void RestoreMode_Negative_Avoid_SetsTenPercent()
    {
        player.SetState(current: 20, _ignored: 0, god: false);
        GuildManagerTestInstaller.InstallAvoidDeathGuild(GameManager.Instance.GuildManager, avoidDeath: true);

        int ret = player.SetHealth(amount: -5, restoreMode: true); // raw set -> <= 0 -> AvoidDeath

        int expected = (int)(player.GetMax() * 0.1f);
        Assert.AreEqual(expected, ret);
        Assert.AreEqual(expected, player.GetCurrent());
        Assert.IsFalse(player.DeathRaised);
    }

    [Test]
    public void RestoreMode_Negative_NoAvoid_RaisesDeath()
    {
        player.SetState(current: 20, _ignored: 0, god: false);
        GuildManagerTestInstaller.InstallAvoidDeathGuild(GameManager.Instance.GuildManager, avoidDeath: false);

        int ret = player.SetHealth(amount: -1, restoreMode: true);

        Assert.LessOrEqual(ret, 0);
        Assert.IsTrue(player.DeathRaised);
    }

    [Test]
    public void GodMode_IgnoresRestoreAndAmount()
    {
        // skip if godMode member absent on this DFU build
        bool hasGodMode =
            typeof(PlayerEntity).GetField("godMode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null ||
            typeof(PlayerEntity).GetProperty("godMode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null ||
            typeof(PlayerEntity).GetProperty("GodMode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null;

        if (!hasGodMode)
        {
            Assert.Ignore("No 'godMode' on PlayerEntity; skipping.");
        }

        player.SetState(current: 5, _ignored: 0, god: true);

        int ret = player.SetHealth(amount: -999, restoreMode: true); // should still return MaxHealth

        int max = player.GetMax();
        Assert.AreEqual(max, ret);
        Assert.AreEqual(max, player.GetCurrent());
    }

    [Test]
    public void AvoidDeath_NotCalled_WhenHealthPositive()
    {
        // Arrange
        player.SetState(current: 10, _ignored: 0, god: false);
        var fake = GuildManagerTestInstaller.InstallAvoidDeathGuild(GameManager.Instance.GuildManager, avoidDeath: true);

        // Act (restoreMode bypasses clamp even if MaxHealth == 0)
        int ret = player.SetHealth(amount: 25, restoreMode: true);

        // Assert
        Assert.AreEqual(25, ret, "Resulting health should be the raw amount in restoreMode.");
        Assert.AreEqual(25, player.GetCurrent(), "currentHealth should match the raw amount in restoreMode.");
        Assert.AreEqual(0, fake.Calls, "AvoidDeath() must not be queried when resulting health is > 0.");
    }

    [Test]
    public void AvoidDeath_Called_WhenZero()
    {
        player.SetState(current: 10, _ignored: 0, god: false);
        var fake = GuildManagerTestInstaller.InstallAvoidDeathGuild(GameManager.Instance.GuildManager, avoidDeath: false);

        player.SetHealth(amount: 0, restoreMode: false);

        Assert.GreaterOrEqual(fake.Calls, 1, "AvoidDeath() should be queried when health <= 0.");
    }

    [Test]
    public void RestoreMode_AllowsOverheal_AboveMax()
    {
        player.SetState(current: 10, _ignored: 0, god: false);

        // set to a big positive raw amount; should not clamp
        int amount = player.GetMax() + 250;
        int ret = player.SetHealth(amount: amount, restoreMode: true);

        Assert.AreEqual(amount, ret);
        Assert.AreEqual(amount, player.GetCurrent());
    }

    [Test]
    public void NonRestore_LargeNegative_ClampedZero()
    {
        player.SetState(current: 10, _ignored: 0, god: false);
        GuildManagerTestInstaller.InstallAvoidDeathGuild(GameManager.Instance.GuildManager, avoidDeath: false);

        int ret = player.SetHealth(amount: int.MinValue, restoreMode: false);

        Assert.AreEqual(0, ret);
        Assert.AreEqual(0, player.GetCurrent());
        Assert.IsTrue(player.DeathRaised);
    }

    [Test]
    public void TenPercent_RoundedDown_ForOddMax()
    {
        // Try to force MaxHealth to an odd value to observe floor cast
        bool set = player.TrySetMaxHealth(95);
        if (!set)
        {
            Assert.Ignore("Cannot set MaxHealth on this DFU build; skipping rounding test.");
        }

        player.SetState(current: 50, _ignored: 0, god: false);
        GuildManagerTestInstaller.InstallAvoidDeathGuild(GameManager.Instance.GuildManager, avoidDeath: true);

        int ret = player.SetHealth(amount: -1, restoreMode: false);

        int expected = (int)(player.GetMax() * 0.1f); // should floor (95 * 0.1 = 9.5 => 9)
        Assert.AreEqual(expected, ret);
        Assert.AreEqual(expected, player.GetCurrent());
        Assert.IsFalse(player.DeathRaised);
    }

    [Test]
    public void Fatigue_GodMode_AlwaysSetsAndReturns_MaxFatigue()
    {
        // Skip if godMode is absent
        if (!player.TrySetGodMode(true))
            Assert.Ignore("No 'godMode' member on PlayerEntity; skipping fatigue god-mode test.");

        // Act
        int ret = player.SetFatigue(amount: 5, restoreMode: false);

        // Assert
        int maxF = player.GetMaxFatigue();
        Assert.AreEqual(maxF, ret, "In god mode, SetFatigue should return MaxFatigue.");
        Assert.AreEqual(maxF, player.GetCurrentFatigue(), "In god mode, currentFatigue should equal MaxFatigue.");
    }

    [Test]
    public void Fatigue_NonGod_RestoreMode_SetsRaw_AndBypassesClamp()
    {
        // Arrange
        player.TrySetGodMode(false);

        // Act (restoreMode=true should delegate to base and set raw value)
        int ret = player.SetFatigue(amount: 1234, restoreMode: true);

        // Assert
        Assert.AreEqual(1234, ret, "restoreMode should set raw fatigue regardless of MaxFatigue.");
        Assert.AreEqual(1234, player.GetCurrentFatigue(), "currentFatigue should equal the raw amount in restoreMode.");
    }

    [Test]
    public void Magicka_GodMode_AlwaysSetsAndReturns_MaxMagicka()
    {
        // Skip if godMode is absent
        if (!player.TrySetGodMode(true))
            Assert.Ignore("No 'godMode' member on PlayerEntity; skipping magicka god-mode test.");

        // Act
        int ret = player.SetMagicka(amount: 1, restoreMode: true); // restore flag should be ignored in god mode

        // Assert
        int maxM = player.GetMaxMagicka();
        Assert.AreEqual(maxM, ret, "In god mode, SetMagicka should return MaxMagicka.");
        Assert.AreEqual(maxM, player.GetCurrentMagicka(), "In god mode, currentMagicka should equal MaxMagicka.");
    }

    [Test]
    public void Magicka_NonGod_RestoreMode_SetsRaw_AndBypassesClamp()
    {
        // Arrange
        player.TrySetGodMode(false);

        // Act
        int ret = player.SetMagicka(amount: 777, restoreMode: true);

        // Assert
        Assert.AreEqual(777, ret, "restoreMode should set raw magicka regardless of MaxMagicka.");
        Assert.AreEqual(777, player.GetCurrentMagicka(), "currentMagicka should equal the raw amount in restoreMode.");
    }

    // ---------- TallySkill tests ----------
    [Test]
    public void TallySkill_IncrementsWithinBounds()
    {
        // Arrange
        player.InitSkillUses();
        int skillId = (int)DFCareer.Skills.Archery; // pick any valid enum
        player.SetSkillUseAt(skillId, 10);

        // Act
        player.TallySkill(DFCareer.Skills.Archery, 5);

        // Assert
        Assert.AreEqual(15, player.GetSkillUseAt(skillId), "Skill tally should add amount within bounds.");
    }

    [Test]
    public void TallySkill_ClampsToUpperBound_20000()
    {
        // Arrange
        player.InitSkillUses();
        int skillId = (int)DFCareer.Skills.ShortBlade;
        player.SetSkillUseAt(skillId, 19999);

        // Act
        player.TallySkill(DFCareer.Skills.ShortBlade, 10); // 19999 + 10 -> clamp 20000

        // Assert
        Assert.AreEqual(20000, player.GetSkillUseAt(skillId), "Skill tally must clamp to 20000 max.");
    }

    [Test]
    public void TallySkill_ClampsToZero_OnNegativeResult()
    {
        // Arrange
        player.InitSkillUses();
        int skillId = (int)DFCareer.Skills.Destruction;
        player.SetSkillUseAt(skillId, 2);

        // Act
        player.TallySkill(DFCareer.Skills.Destruction, (short)(-10)); // 2 - 10 -> clamp 0

        // Assert
        Assert.AreEqual(0, player.GetSkillUseAt(skillId), "Skill tally must clamp to zero on underflow.");
    }

    // Optional: resilience test — no exception if skillUses is null
    [Test]
    public void TallySkill_DoesNotThrow_WhenSkillArrayIsNull()
    {
        // Arrange
        player.SetSkillUsesNull();

        // Act & Assert: should not throw, just log
        Assert.DoesNotThrow(() => player.TallySkill(DFCareer.Skills.Mysticism, 5));
    }


    // ---------- TallyCrimeGuildRequirements tests ----------

    [Test]
    public void TallyCrime_Thieves_DoesNotSetLetter_BelowThreshold()
    {
        // Arrange: tally below threshold (needs >=10), ensure no previous letter set
        player.SetThievesTally(5);
        Assert.AreEqual(0u, player.GetThievesLetterTime(), "Precondition: no thieves letter scheduled.");

        // Act: +4 -> 9 < 10, should NOT schedule letter
        player.TallyCrimeGuildRequirements(thievingCrime: true, amount: 4);

        // Assert
        Assert.AreEqual(0u, player.GetThievesLetterTime(), "Thieves letter must not be set below threshold.");
    }

    [Test]
    public void TallyCrime_Thieves_SetsLetter_AtThreshold()
    {
        // Arrange: 9 + 1 -> reach threshold and schedule in 3 days (4320 minutes)
        player.SetThievesTally(9);
        Assert.AreEqual(0u, player.GetThievesLetterTime(), "Precondition: no thieves letter scheduled.");

        // Act
        var before = DaggerfallUnity.Instance.WorldTime.DaggerfallDateTime.ToClassicDaggerfallTime();
        player.TallyCrimeGuildRequirements(thievingCrime: true, amount: 1);
        var scheduled = player.GetThievesLetterTime();

        // Assert: scheduled should be >= now + 4320 (exactly +4320 в текущей реализации)
        Assert.GreaterOrEqual(scheduled, before + 4320, "Thieves letter should be scheduled 3 days ahead.");
    }

    [Test]
    public void TallyCrime_Dark_SetsLetter_AtThreshold()
    {
        // Arrange: DB needs >=15; set 14 + 1 -> schedule
        player.SetDarkTally(14);
        Assert.AreEqual(0u, player.GetDarkLetterTime(), "Precondition: no DB letter scheduled.");

        // Act
        var before = DaggerfallUnity.Instance.WorldTime.DaggerfallDateTime.ToClassicDaggerfallTime();
        player.TallyCrimeGuildRequirements(thievingCrime: false, amount: 1);
        var scheduled = player.GetDarkLetterTime();

        // Assert
        Assert.GreaterOrEqual(scheduled, before + 4320, "Dark Brotherhood letter should be scheduled 3 days ahead.");
    }
}

[TestFixture]
public class PlayerEntityPropertyTests
{
    private TestablePlayerEntity player;

    [SetUp]
    public void Setup()
    {
        var go = new GameObject("PlayerEntity_Props_Test");
        var dummy = go.AddComponent<DaggerfallEntityBehaviour>();
        player = new TestablePlayerEntity(dummy, false);
    }

    [TearDown]
    public void TearDown()
    {
        UnityEngine.Object.DestroyImmediate(player.gameObject);
    }

    [Test]
    public void BoolProperties_SetAndGet_AreConsistent()
    {
        player.NoClipMode = true;
        player.NoTargetMode = true;
        player.PreventEnemySpawns = true;
        player.PreventNormalizingReputations = true;
        player.IsResting = true;
        player.IsLoitering = true;
        player.ReadyToLevelUp = true;
        player.OghmaLevelUp = true;
        player.InPrison = true;
        player.IsInBeastForm = true;

        Assert.IsTrue(player.NoClipMode);
        Assert.IsTrue(player.NoTargetMode);
        Assert.IsTrue(player.PreventEnemySpawns);
        Assert.IsTrue(player.PreventNormalizingReputations);
        Assert.IsTrue(player.IsResting);
        Assert.IsTrue(player.IsLoitering);
        Assert.IsTrue(player.ReadyToLevelUp);
        Assert.IsTrue(player.OghmaLevelUp);
        Assert.IsTrue(player.InPrison);
        Assert.IsTrue(player.IsInBeastForm);
    }

    [Test]
    public void NumericProperties_SetAndGet_AreConsistent()
    {
        player.FaceIndex = 7;
        player.GoldPieces = 999;
        player.BiographyReactionMod = 12;
        player.BiographyResistMagicMod = 5;
        player.StartingLevelUpSkillSum = 50;
        player.CurrentLevelUpSkillSum = 75;
        player.TimeOfLastSkillTraining = 12345;
        player.TimeOfLastStealthCheck = 5555;
        player.TimeOfLastSkillIncreaseCheck = 111;
        player.ThievesGuildRequirementTally = 10;
        player.DarkBrotherhoodRequirementTally = 15;
        player.TimeForThievesGuildLetter = 222;
        player.TimeForDarkBrotherhoodLetter = 333;
        player.LastGameMinutes = 444;

        Assert.AreEqual(7, player.FaceIndex);
        Assert.AreEqual(999, player.GoldPieces);
        Assert.AreEqual(12, player.BiographyReactionMod);
        Assert.AreEqual(5, player.BiographyResistMagicMod);
        Assert.AreEqual(50, player.StartingLevelUpSkillSum);
        Assert.AreEqual(75, player.CurrentLevelUpSkillSum);
        Assert.AreEqual(12345u, player.TimeOfLastSkillTraining);
        Assert.AreEqual(5555u, player.TimeOfLastStealthCheck);
        Assert.AreEqual(111u, player.TimeOfLastSkillIncreaseCheck);
        Assert.AreEqual(10, player.ThievesGuildRequirementTally);
        Assert.AreEqual(15, player.DarkBrotherhoodRequirementTally);
        Assert.AreEqual(222u, player.TimeForThievesGuildLetter);
        Assert.AreEqual(333u, player.TimeForDarkBrotherhoodLetter);
        Assert.AreEqual(444u, player.LastGameMinutes);
    }

    [Test]
    public void CollectionsAndRefs_SetAndGet_AreConsistent()
    {
        var wagon = new ItemCollection();
        var other = new ItemCollection();
        var rooms = new List<RoomRental_v1>();
        var region = new PlayerEntity.RegionDataRecord[1];

        // Just to make the collections non-empty (so ReplaceAll works)
        wagon.AddItem(new DaggerfallUnityItem());  // <- fake basic item (no localization)
        other.AddItem(new DaggerfallUnityItem());

        player.WagonItems = wagon;
        player.OtherItems = other;
        player.RentedRooms = rooms;
        player.RegionData = region;

        Assert.AreEqual(wagon.Count, player.WagonItems.Count, "WagonItems should copy elements via ReplaceAll().");
        Assert.AreEqual(other.Count, player.OtherItems.Count, "OtherItems should copy elements via ReplaceAll().");
        Assert.AreSame(rooms, player.RentedRooms);
        Assert.AreSame(region, player.RegionData);
    }

    [Test]
    public void ComplexReferenceProperties_SetAndGet_AreConsistent()
    {
        var light = new DaggerfallUnityItem();
        var race = new RaceTemplate() { ID = 3 };
        var anchor = new PlayerPositionData_v1();

        player.LightSource = light;
        player.BirthRaceTemplate = race;
        player.AnchorPosition = anchor;
        player.PreviousVampireClan = VampireClans.None;

        // use existing value from your enum
        player.Reflexes = PlayerReflexes.Average;

        Assert.AreSame(light, player.LightSource);
        Assert.AreSame(race, player.BirthRaceTemplate);
        Assert.AreSame(anchor, player.AnchorPosition);
        Assert.AreEqual(VampireClans.None, player.PreviousVampireClan);
        Assert.AreEqual(PlayerReflexes.Average, player.Reflexes);
    }

    [Test]
    public void BackStory_CanBeAssigned()
    {
        var list = new List<string> { "childhood", "destiny" };
        player.BackStory = list;
        Assert.AreEqual(2, player.BackStory.Count);
        Assert.AreEqual("childhood", player.BackStory[0]);
    }
}
