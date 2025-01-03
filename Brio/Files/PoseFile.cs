﻿using Brio.Capabilities.Posing;
using Brio.Core;
using Brio.Entities;
using Brio.Entities.Actor;
using Brio.Files.Converters;
using Brio.Game.Posing;
using Brio.Library.Sources;
using Brio.Resources;
using Brio.UI.Controls.Editors;
using Brio.UI.Controls.Stateless;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;
using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Brio.Files;

public class PoseFileInfo : AppliableActorFileInfoBase<PoseFile>
{
    private PosingService _posingService;

    public override string Name => "Pose File";
    public override IDalamudTextureWrap Icon => ResourceProvider.Instance.GetResourceImage("Images.FileIcon_Pose.png");
    public override string Extension => ".pose";

    public PoseFileInfo(EntityManager entityManager, PosingService posingService)
    : base(entityManager)
    {
        _posingService = posingService;
    }

    public override void DrawActions(FileEntry fileEntry, bool isModal)
    {
        if(ImBrio.Button("", FontAwesomeIcon.Cog, new Vector2(25, 0), hoverText: "Import Options"))
        {
            ImGui.OpenPopup("import_options_popup_lib");
        }

        using(var popup = ImRaii.Popup("import_options_popup_lib"))
        {
            if(popup.Success)
            {
                PosingEditorCommon.DrawImportOptionEditor(_posingService.DefaultImporterOptions);
            }
        }

        ImGui.SameLine();

        base.DrawActions(fileEntry, isModal);
    }

    protected override void Apply(PoseFile file, ActorEntity actor, bool asExpression)
    {
        PosingCapability? capability;
        if(actor.TryGetCapability<PosingCapability>(out capability) && capability != null)
        {
            capability.ImportPose(file, asExpression: asExpression);
        }
    }
}

[Serializable]
public class PoseFile : JsonDocumentBase
{
    public Bone ModelDifference { get; set; } = Transform.Identity;

    public Dictionary<string, Bone> Bones { get; set; } = [];
    public Dictionary<string, Bone> MainHand { get; set; } = [];
    public Dictionary<string, Bone> OffHand { get; set; } = [];

    public class Bone
    {
        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; }
        public Vector3 Scale { get; set; }

        public static implicit operator Transform(Bone bone)
        {
            return new Transform()
            {
                Position = bone.Position,
                Rotation = bone.Rotation,
                Scale = bone.Scale
            };
        }

        public static implicit operator Bone(Transform bone)
        {
            return new Bone()
            {
                Position = bone.Position,
                Rotation = bone.Rotation,
                Scale = bone.Scale
            };
        }
    }

    public void SanitizeBoneNames()
    {
        var newBones = new Dictionary<string, Bone>();
        foreach(var bone in Bones)
        {
            newBones[AnamnesisBoneNameConverter.AnamnesisToGame(bone.Key)] = bone.Value;
        }
        Bones = newBones;
    }
}
