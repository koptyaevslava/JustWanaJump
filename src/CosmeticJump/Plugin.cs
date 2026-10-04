using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;

namespace CosmeticJump;

[BepInPlugin(PluginId, PluginName, PluginVersion)]
public sealed class Plugin : BasePlugin
{
    public const string PluginId = "local.seaofstars.cosmeticjump";
    public const string PluginName = "JustWanaJump";
    public const string PluginVersion = "0.2.1";

    private Harmony harmony;

    public override void Load()
    {
        ModSettings.Initialize(Config);
        CosmeticJumpRuntime.SetLogger(Log);

        harmony = new Harmony(PluginId);
        harmony.PatchAll();
        CosmeticJumpModsMenu.Install(harmony, Log);
        AddComponent<CosmeticJumpDriver>();

        Log.LogInfo("JustWanaJump 0.2.1 loaded. The normal Interact button jumps while free-roaming.");
    }

    public override bool Unload()
    {
        CosmeticJumpRuntime.Reset();
        CosmeticJumpModsMenu.Dispose();
        harmony?.UnpatchSelf();
        return true;
    }
}

internal static class ModSettings
{
    public static ConfigEntry<bool> Enabled { get; private set; }
    public static ConfigEntry<float> HeightMultiplier { get; private set; }
    public static ConfigEntry<float> DurationSeconds { get; private set; }

    public static void Initialize(ConfigFile config)
    {
        Enabled = config.Bind("General", "Enabled", true,
            "Allow cosmetic jumps during normal exploration.");
        HeightMultiplier = config.Bind("General", "HeightMultiplier", 1.0f,
            "Multiplier for the native visual jump height (0.5 to 2.0 recommended).");
        DurationSeconds = config.Bind("General", "DurationSeconds", 0.6f,
            "Full cosmetic jump duration in seconds (0.3 to 0.6).");
        DurationSeconds.Value = Mathf.Clamp(DurationSeconds.Value, 0.3f, 0.6f);
    }
}

public sealed class CosmeticJumpDriver : MonoBehaviour
{
    public void LateUpdate()
    {
        CosmeticJumpRuntime.LateTick();
    }

    public void OnDisable()
    {
        CosmeticJumpRuntime.Reset();
    }
}

[HarmonyPatch(typeof(PlayerDefaultState), nameof(PlayerDefaultState.StateExecute))]
internal static class PlayerDefaultStateExecutePatch
{
    private static void Prefix(PlayerDefaultState __instance, out bool __state)
    {
        __state = CosmeticJumpRuntime.WasJumpPressed(__instance);
    }

    private static void Postfix(PlayerDefaultState __instance, bool __state)
    {
        CosmeticJumpRuntime.TryStart(__instance, __state);
    }
}

[HarmonyPatch(typeof(PlayerDefaultState), "PlayMoveAnim")]
internal static class PlayerDefaultStatePlayMoveAnimPatch
{
    private static bool Prefix(PlayerDefaultState __instance)
    {
        return !CosmeticJumpRuntime.IsJumping(__instance?.PlayerController);
    }
}

internal static class CosmeticJumpRuntime
{
    private const string FullJumpState = "Base Layer.Navigation.JumpUp";
    private const string FullRunState = "Base Layer.Navigation.Run";
    private const string FullWalkState = "Base Layer.Navigation.Walk";
    private const string FullIdleState = "Base Layer.Idle";
    private const float FallbackHeight = 0.75f;
    private const float MinimumDuration = 0.30f;
    private const float MaximumDuration = 0.60f;
    private const float MinimumHeight = 0.10f;
    private const float MaximumHeight = 3.00f;

    private static ManualLogSource log;
    private static PlayerController player;
    private static PlayerDefaultState defaultState;
    private static Transform visualTransform;
    private static float startedAt;
    private static float duration;
    private static float height;
    private static float appliedOffset;
    private static int jumpStateHash;
    private static int runStateHash;
    private static int walkStateHash;
    private static int idleStateHash;
    private static bool warnedMissingAnimation;

    public static void SetLogger(ManualLogSource logger)
    {
        log = logger;
        jumpStateHash = Animator.StringToHash(FullJumpState);
        runStateHash = Animator.StringToHash(FullRunState);
        walkStateHash = Animator.StringToHash(FullWalkState);
        idleStateHash = Animator.StringToHash(FullIdleState);
    }

    public static bool IsJumping(PlayerController candidate)
    {
        return player != null && candidate != null && player.Pointer == candidate.Pointer;
    }

    public static bool WasJumpPressed(PlayerDefaultState state)
    {
        if (!ModSettings.Enabled.Value || state == null || player != null)
            return false;

        try
        {
            PlayerController candidate = state.PlayerController;
            return candidate != null && candidate.PlayerInputs != null && candidate.PlayerInputs.GetInteractDown();
        }
        catch
        {
            return false;
        }
    }

    public static void TryStart(PlayerDefaultState state, bool wasPressed)
    {
        if (!wasPressed || !ModSettings.Enabled.Value || state == null || player != null)
            return;

        PlayerController candidate;
        try
        {
            candidate = state.PlayerController;
            if (candidate == null || candidate.stateMachine == null ||
                candidate.stateMachine.CurrentState == null ||
                candidate.stateMachine.CurrentState.Pointer != state.Pointer)
                return;

            // Interactions and native navigation transitions always take priority.
            // The cosmetic jump is only the fallback for an otherwise-unused press.
            if (candidate.IsInsideUsableInteractionTrigger())
                return;

        }
        catch
        {
            return;
        }

        Start(candidate, state);
    }

    private static void Start(PlayerController candidate, PlayerDefaultState state)
    {
        Transform safeVisual = ResolveVisualTransform(candidate);
        Animator animator = candidate.animator;
        if (safeVisual == null || animator == null)
            return;

        if (!animator.HasState(0, jumpStateHash))
        {
            if (!warnedMissingAnimation)
            {
                warnedMissingAnimation = true;
                log?.LogWarning($"Animator '{animator.name}' has no state '{FullJumpState}'. Jump was skipped.");
            }
            return;
        }

        float nativeHeight = FallbackHeight;
        JumpValues values = candidate.jumpValues;
        if (values != null)
        {
            if (values.currentJumpHeight > 0.01f)
                nativeHeight = values.currentJumpHeight;
        }

        height = Mathf.Clamp(nativeHeight * Mathf.Clamp(ModSettings.HeightMultiplier.Value, 0.1f, 4f),
            MinimumHeight, MaximumHeight);
        duration = Mathf.Clamp(ModSettings.DurationSeconds.Value, MinimumDuration, MaximumDuration);
        startedAt = Time.unscaledTime;
        appliedOffset = 0f;
        player = candidate;
        defaultState = state;
        visualTransform = safeVisual;

        animator.Play(jumpStateHash, 0, 0f);
    }

    public static void LateTick()
    {
        if (player == null)
            return;

        try
        {
            if (!ModSettings.Enabled.Value || visualTransform == null || player.stateMachine == null ||
                player.stateMachine.CurrentState == null ||
                defaultState == null || player.stateMachine.CurrentState.Pointer != defaultState.Pointer)
            {
                Reset();
                return;
            }

            float progress = Mathf.Clamp01((Time.unscaledTime - startedAt) / duration);
            float nextOffset = 4f * height * progress * (1f - progress);
            ApplyOffset(nextOffset);

            if (progress >= 1f)
            {
                RestoreLocomotionAnimation();
                Reset();
            }
        }
        catch
        {
            Reset();
        }
    }

    private static Transform ResolveVisualTransform(PlayerController candidate)
    {
        Transform root = candidate.transform;
        Transform target = null;

        if (candidate.lookDirectionController != null &&
            candidate.lookDirectionController.characterVisual != null)
        {
            target = candidate.lookDirectionController.characterVisual.transform;
            if (target != null && root != null && target.Pointer != root.Pointer)
                return target;
        }

        if (candidate.playerSpriteRenderer != null)
        {
            target = candidate.playerSpriteRenderer.transform;
            if (target != null && root != null && target.Pointer != root.Pointer)
                return target;
        }

        target = candidate.animationMovementRef;
        if (target != null && root != null && target.Pointer != root.Pointer)
            return target;

        if (candidate.animator != null)
        {
            target = candidate.animator.transform;
            if (target != null && root != null && target.Pointer != root.Pointer)
                return target;
        }

        return null;
    }

    private static void ApplyOffset(float nextOffset)
    {
        Vector3 position = visualTransform.localPosition;
        position.y += nextOffset - appliedOffset;
        visualTransform.localPosition = position;
        appliedOffset = nextOffset;
    }

    private static void RestoreLocomotionAnimation()
    {
        if (player == null || defaultState == null || player.animator == null ||
            player.stateMachine == null || player.stateMachine.CurrentState == null ||
            player.stateMachine.CurrentState.Pointer != defaultState.Pointer)
            return;

        float inputLength = 0f;
        try
        {
            if (player.PlayerInputs != null)
                inputLength = player.PlayerInputs.GetMovementInputLength();
        }
        catch
        {
            inputLength = 0f;
        }

        int stateHash;
        if (inputLength <= 0.01f)
        {
            stateHash = idleStateHash;
        }
        else
        {
            float walkThreshold = defaultState.inputLengthWalkThreshold;
            stateHash = walkThreshold > 0f && inputLength <= walkThreshold ? walkStateHash : runStateHash;
        }

        if (player.animator.HasState(0, stateHash))
            player.animator.Play(stateHash, 0, 0f);
    }

    public static void Reset()
    {
        if (visualTransform != null && Mathf.Abs(appliedOffset) > 0.0001f)
        {
            try
            {
                ApplyOffset(0f);
            }
            catch
            {
                // The scene may have destroyed the player between frames.
            }
        }

        player = null;
        defaultState = null;
        visualTransform = null;
        startedAt = 0f;
        duration = 0f;
        height = 0f;
        appliedOffset = 0f;
    }
}
