using Brio.Core;
using Brio.Game.Actor.Extensions;
using Brio.Game.Core;
using Brio.Game.GPose;
using Brio.Game.Types;
using Brio.IPC;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using static FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.VertexShader;
using System.Text.RegularExpressions;
using CharacterCopyFlags = FFXIVClientStructs.FFXIV.Client.Game.Character.CharacterSetupContainer.CopyFlags;
using ClientObjectManager = FFXIVClientStructs.FFXIV.Client.Game.Object.ClientObjectManager;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace Brio.Game.Actor;

public class ActorSpawnService : IDisposable
{
    private readonly ObjectMonitorService _monitorService;
    private readonly IObjectTable _objectTable;
    private readonly IClientState _clientState;
    private readonly IFramework _framework;
    private readonly GPoseService _gPoseService;
    private readonly ActorRedrawService _actorRedrawService;
    private readonly GlamourerService _glamourerService;
    private readonly TargetService _targetService;

    private readonly List<ushort> _createdIndexes = [];

    public TargetService TargetService => _targetService;

    public unsafe ActorSpawnService(ObjectMonitorService monitorService, GlamourerService glamourerService, IObjectTable objectTable, IClientState clientState, IFramework framework, GPoseService gPoseService, ActorRedrawService actorRedrawService, TargetService targetService)
    {
        _monitorService = monitorService;
        _objectTable = objectTable;
        _clientState = clientState;
        _framework = framework;
        _gPoseService = gPoseService;
        _actorRedrawService = actorRedrawService;
        _glamourerService = glamourerService;
        _targetService = targetService;

        _monitorService.CharacterDestroyed += OnCharacterDestroyed;
        _gPoseService.OnGPoseStateChange += OnGPoseStateChanged;
        _clientState.TerritoryChanged += OnTerritoryChanged;
    }

    public bool CreateCharacter([MaybeNullWhen(false)] out ICharacter outCharacter, SpawnFlags flags = SpawnFlags.Default, bool disableSpawnCompanion = false, Vector3 position = new Vector3(), float rotation = 0)
    {
        outCharacter = null;

        var localPlayer = _clientState.LocalPlayer;
        if(localPlayer != null)
        {
            if(CloneCharacter(localPlayer, out outCharacter, flags, disableSpawnCompanion: disableSpawnCompanion, position, rotation))
            {
                return true;
            }
        }

        return false;
    }

    public unsafe bool CloneCharacter(ICharacter sourceCharacter, [MaybeNullWhen(false)] out ICharacter outCharacter, SpawnFlags flags = SpawnFlags.Default, bool disableSpawnCompanion = false, Vector3 position = new Vector3(), float rotation = 0)
    {
        outCharacter = null;

        CharacterCopyFlags copyFlags = CharacterCopyFlags.WeaponHiding;

        bool hasCompanion = sourceCharacter.HasSpawnedCompanion();
        if(disableSpawnCompanion == false && hasCompanion)
        {
            flags |= SpawnFlags.ReserveCompanionSlot;
            copyFlags |= CharacterCopyFlags.Companion | CharacterCopyFlags.Ornament | CharacterCopyFlags.Mount;
        }

        if(flags.HasFlag(SpawnFlags.CopyPosition))
            copyFlags |= CharacterCopyFlags.Position;


        if(CreateEmptyCharacter(out outCharacter, flags))
        {

            var sourceNative = sourceCharacter.Native();
            var targetNative = outCharacter.Native();

            // This double copy from self is needed for some tools like Penumbra/Glamourer.
            // We first copy the real source, then we copy ourselves onto ourselves.
            targetNative->CharacterSetup.CopyFromCharacter(sourceCharacter.Native(), copyFlags);
            targetNative->CharacterSetup.CopyFromCharacter(outCharacter.Native(), CharacterCopyFlags.None);

            // Copy position if requested
            if(flags.HasFlag(SpawnFlags.CopyPosition))
            {
                position = sourceNative->GameObject.Position;
                rotation = sourceNative->GameObject.Rotation;

                // TODO: This is only needed for Anamnesis and Ktisis. 
                if(sourceNative->GameObject.DrawObject != null && sourceNative->GameObject.DrawObject->IsVisible)
                {
                    // TODO: This is weird if you are mounted
                    //position = sourceNative->GameObject.DrawObject->Object.Position;
                }

                targetNative->GameObject.DefaultPosition = position;
                targetNative->GameObject.Position = position;
                targetNative->GameObject.Rotation = rotation;
                targetNative->GameObject.DefaultRotation = rotation;
            }
            if(flags.HasFlag(SpawnFlags.DefinePosition))
            {
                // TODO: This is only needed for Anamnesis and Ktisis. 
                if(sourceNative->GameObject.DrawObject != null && sourceNative->GameObject.DrawObject->IsVisible)
                {
                    // TODO: This is weird if you are mounted
                    // position = sourceNative->GameObject.DrawObject->Object.Position;
                }

                targetNative->GameObject.DefaultPosition = position;
                targetNative->GameObject.Position = position;
                targetNative->GameObject.Rotation = rotation;
                targetNative->GameObject.DefaultRotation = rotation;
            }

            // Start drawing
            _actorRedrawService.DrawWhenReady(outCharacter);

            if(disableSpawnCompanion == false && hasCompanion)
            {
                // We need to wait for the companion to be ready before we can draw it.
                var companion = _objectTable.CreateObjectReference((nint)(targetNative->CompanionObject));
                if(companion != null)
                    _actorRedrawService.DrawWhenReady(companion);
            }


            return true;
        }

        return false;
    }

    public void ClearAll()
    {
        for(int i = ActorTableHelpers.GPoseStart; i <= ActorTableHelpers.GPoseEnd; i++)
        {
            var obj = _objectTable[i];
            if(obj == null)
                continue;

            DestroyObject(obj);
        }
    }

    public bool DestroyObject(int objectIndex)
    {
        var go = _objectTable[objectIndex];

        if(go != null)
            return DestroyObject(go);

        return false;
    }

    public unsafe bool DestroyObject(IGameObject go)
    {
        Brio.Log.Debug($"Destroying gameobjectobject {go.ObjectIndex}...");

        var com = ClientObjectManager.Instance();
        var native = go.Native();
        var idx = com->GetIndexByObject(native);
        if(idx != 0xFFFFFFFF)
        {
            com->DeleteObjectByIndex((ushort)idx, 0);
            return true;
        }

        return false;
    }

    public unsafe void DestroyAllCreated()
    {
        Brio.Log.Debug("Destroying all created gameobjects.");

        var indexes = _createdIndexes.ToList();
        var com = ClientObjectManager.Instance();
        foreach(var idx in indexes)
        {
            com->DeleteObjectByIndex(idx, 0);
        }
        _createdIndexes.Clear();
    }

    public void DestroyCompanion(ICharacter character)
    {
        if(character.CalculateCompanionInfo(out var info))
        {
            publicSetCompanion(character, info.Kind, 0);
        }
    }

    public unsafe void CreateCompanion(ICharacter character, CompanionContainer container)
    {
        DestroyCompanion(character);
        publicSetCompanion(character, container.Kind, (short)container.Id);

        // We need to wait for the companion to be ready before we can draw it.
        var companionNative = &character.Native()->CompanionObject->Character.GameObject;
        _framework.RunUntilSatisfied(
            () => character.CalculateCompanionInfo(out var info) && info.Kind == container.Kind && info.Id == container.Id && companionNative->IsReadyToDraw(),
            (_) => companionNative->EnableDraw(),
            1000,
            dontStartFor: 1
        );
    }

    private unsafe void publicSetCompanion(ICharacter character, CompanionKind kind, short id)
    {
        var native = character.Native();
        switch(kind)
        {
            case CompanionKind.Companion:
                native->CompanionData.SetupCompanion(id, 0);
                break;

            case CompanionKind.Mount:
                native->Mount.CreateAndSetupMount(id, 0, 0, 0, 0, 0, 0);
                break;

            case CompanionKind.Ornament:
                native->OrnamentData.SetupOrnament(id, 0);
                break;
        }
    }

    private bool CreateEmptyCharacter([MaybeNullWhen(false)] out ICharacter outCharacter, SpawnFlags flags)
    {
        outCharacter = null;

        Brio.Log.Debug("Creating Brio character...");

        unsafe
        {
            var com = ClientObjectManager.Instance();
            uint idCheck = com->CreateBattleCharacter((uint)(2 + _createdIndexes.Count), param: (byte)(flags.HasFlag(SpawnFlags.ReserveCompanionSlot) ? 1 : 0));
            if(idCheck == 0xffffffff)
            {
                Brio.Log.Warning("Failed to create character, invalid ID was returned.");
                EventBus.Instance.NotifyError("Failed to create character.");
                return false;
            }
            ushort newId = (ushort)idCheck;
            int count = _createdIndexes.Count;
            _createdIndexes.Add(newId);

            var newObject = com->GetObjectByIndex(newId);
            if(newObject == null) return false;
            var newPlayer = (NativeCharacter*)newObject;

            string raw = "";
            switch(count)
            {
                case 0:
                    raw = "Cutscene Player";
                    break;
                default:
                    raw = "Reborn" + Regex.Replace(Guid.NewGuid().ToString(), @"[\d-]", string.Empty).Replace("-", "");
                    break;
            }
            string name = raw.Substring(0, Math.Clamp(raw.Length, 0, 14));
            int length = name.Length / 2;
            newObject->SetName(count == 0 ? raw : FirstCharToUpper(name.Substring(0, length)) + " " + FirstCharToUpper(name.Substring(length))); // Brio One etc

            //_gPoseService.AddCharacterToGPose(newPlayer);

            var character = _objectTable.CreateObjectReference((nint)newObject);
            if(character == null || character is not ICharacter)
                return false;

            outCharacter = (ICharacter)character;
        }

        return true;
    }
    public static string FirstCharToUpper(string input)
    {
        if(String.IsNullOrEmpty(input))
            throw new ArgumentException("ARGH!");
        return input.First().ToString().ToUpper() + String.Join("", input.Skip(1));
    }
    private void OnGPoseStateChanged(bool newState)
    {
        if(!newState)
            DestroyAllCreated();
    }

    private unsafe void OnCharacterDestroyed(NativeCharacter* chara)
    {
        var go = _objectTable.CreateObjectReference((nint)chara);
        if(go != null && go.IsGPose())
        {
            var com = ClientObjectManager.Instance();
            var idx = com->GetIndexByObject(go.Native());
            if(idx < ushort.MaxValue)
                _createdIndexes.Remove((ushort)idx);
        }
    }

    private void OnTerritoryChanged(ushort obj)
    {
        _createdIndexes.Clear();
    }

    public unsafe void Dispose()
    {
        _monitorService.CharacterDestroyed -= OnCharacterDestroyed;
        _gPoseService.OnGPoseStateChange -= OnGPoseStateChanged;
        _clientState.TerritoryChanged -= OnTerritoryChanged;
        if(_clientState.IsLoggedIn)
        {
            DestroyAllCreated();
        }
    }
}

[Flags]
public enum SpawnFlags
{
    None = 0,
    ReserveCompanionSlot = 1 << 0,
    CopyPosition = 1 << 1,
    DefinePosition = 1 << 2,

    Default = None,
}
