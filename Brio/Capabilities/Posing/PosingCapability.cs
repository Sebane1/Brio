﻿using Brio.Capabilities.Actor;
using Brio.Config;
using Brio.Core;
using Brio.Entities.Actor;
using Brio.Entities.Core;
using Brio.Files;
using Brio.Game.Posing;
using Brio.Input;
using Brio.Resources;
using Brio.UI.Widgets.Posing;
using Brio.UI.Windows.Specialized;
using Dalamud.Plugin.Services;
using OneOf;
using OneOf.Types;
using System.Collections.Generic;
using System.Linq;

namespace Brio.Capabilities.Posing;

internal class PosingCapability : ActorCharacterCapability
{
    public PosingSelectionType Selected { get; set; } = new None();
    public PosingSelectionType Hover { get; set; } = new None();
    public PosingSelectionType LastHover { get; set; } = new None();

    public SkeletonPosingCapability SkeletonPosing => Entity.GetCapability<SkeletonPosingCapability>();
    public ModelPosingCapability ModelPosing => Entity.GetCapability<ModelPosingCapability>();

    public bool HasOverride
    {
        get
        {
            if(Entity.TryGetCapability<SkeletonPosingCapability>(out var skeletonPosing))
                if(skeletonPosing.PoseInfo.IsOveridden)
                    return true;

            if(Entity.TryGetCapability<ModelPosingCapability>(out var modelPosing))
                if(modelPosing.HasOverride)
                    return true;

            return false;
        }
    }

    public bool HasUndoStack => _undoStack.Count > 1;
    public bool HasRedoStack => _redoStack.Any();
    public bool HasIKApplied => SkeletonPosing.PoseInfo.HasIKStacks;

    private Stack<PoseStack> _undoStack = [];
    private Stack<PoseStack> _redoStack = [];

    public bool OverlayOpen
    {
        get => _overlayWindow.IsOpen;
        set => _overlayWindow.IsOpen = value;
    }

    public bool TransformWindowOpen
    {
        get => _overlayTransformWindow.IsOpen;
        set => _overlayTransformWindow.IsOpen = value;
    }

    private readonly PosingOverlayWindow _overlayWindow;
    private readonly PosingService _posingService;
    private readonly ConfigurationService _configurationService;
    private readonly PosingTransformWindow _overlayTransformWindow;
    private readonly IFramework _framework;
    private readonly InputService _input;

    public PosingCapability(
        ActorEntity parent,
        PosingOverlayWindow window,
        PosingService posingService,
        ConfigurationService configurationService,
        PosingTransformWindow overlayTransformWindow,
        IFramework framework,
        InputService input)
        : base(parent)
    {
        Widget = new PosingWidget(this);
        _overlayWindow = window;
        _posingService = posingService;
        _configurationService = configurationService;
        _overlayTransformWindow = overlayTransformWindow;
        _framework = framework;
        _input = input;
    }

    public override void OnEntitySelected()
    {
        base.OnEntitySelected();

        _input.AddListener(KeyBindEvents.Posing_ToggleOverlay, ToggleOverlay);
        _input.AddListener(KeyBindEvents.Posing_Undo, Undo);
        _input.AddListener(KeyBindEvents.Posing_Redo, Redo);
    }

    public override void OnEntityDeselected()
    {
        base.OnEntityDeselected();

        _input.RemoveListener(KeyBindEvents.Posing_ToggleOverlay, ToggleOverlay);
        _input.RemoveListener(KeyBindEvents.Posing_Undo, Undo);
        _input.RemoveListener(KeyBindEvents.Posing_Redo, Redo);
    }

    public void ClearSelection() => Selected = PosingSelectionType.None;

    public void ImportPose(string path, PoseImporterOptions? options = null)
    {
        try
        {
            if(path.EndsWith(".cmp"))
            {
                ImportPose(ResourceProvider.Instance.GetFileDocument<CMToolPoseFile>(path), options, reset: false, reconcile: false);
                return;
            }

            ImportPose(ResourceProvider.Instance.GetFileDocument<PoseFile>(path), options, reset: false, reconcile: false);
        }
        catch
        {
            Brio.NotifyError("Invalid pose file.");
        }
    }

    public void ImportPose(OneOf<PoseFile, CMToolPoseFile> rawPoseFile, PoseImporterOptions? options = null, bool asExpression = false)
    {
        ImportPose(rawPoseFile, options, reset: false, reconcile: false, asExpression: asExpression);
    }

    PoseFile? tempPose;
    private void ImportPose(OneOf<PoseFile, CMToolPoseFile> rawPoseFile, PoseImporterOptions? options = null, bool generateSnapshot = true, bool reset = true, bool reconcile = true,
        bool asExpression = false, bool expressionPhase2 = false)
    {
        var poseFile = rawPoseFile.Match(
                poseFile => poseFile,
                cmToolPoseFile => cmToolPoseFile.Upgrade()
            );

        if(poseFile.Bones.Count == 0 && poseFile.MainHand.Count == 0 && poseFile.OffHand.Count == 0)
        {
            Brio.NotifyError("Invalid pose file.");
            return;
        }

        poseFile.SanitizeBoneNames();

        if(asExpression)
        {
            Brio.Log.Info("Loading as Expression");

            options = _posingService.ExpressionOptions;
            tempPose = GeneratePoseFile();
        }
        else
        {
            options ??= _posingService.DefaultImporterOptions;
        }

        if(options.ApplyModelTransform && reset)
            ModelPosing.ResetTransform();

        SkeletonPosing.ImportSkeletonPose(poseFile, options, expressionPhase2);

        if(asExpression == false)
            ModelPosing.ImportModelPose(poseFile, options);

        if(generateSnapshot)
            _framework.RunOnTick(() => Snapshot(reset, reconcile, asExpression: asExpression), delayTicks: 4);
    }

    public void ExportPose(string path)
    {
        var poseFile = GeneratePoseFile();
        ResourceProvider.Instance.SaveFileDocument(path, poseFile);
    }

    public void Snapshot(bool reset = true, bool reconcile = true, bool asExpression = false)
    {
        var undoStackSize = _configurationService.Configuration.Posing.UndoStackSize;
        if(undoStackSize <= 0)
        {
            _undoStack.Clear();
            _redoStack.Clear();
            return;
        }
      
        _redoStack.Clear();

        if(asExpression == true)
        {
            ImportPose(tempPose!, new PoseImporterOptions(new BoneFilter(_posingService), TransformComponents.All, false),
            generateSnapshot: true, expressionPhase2: true);

            return;
        }

        if(_undoStack.Count == 0)
            _undoStack.Push(new PoseStack(new PoseInfo(), ModelPosing.OriginalTransform));

        _undoStack.Push(new PoseStack(SkeletonPosing.PoseInfo.Clone(), ModelPosing.Transform));
        _undoStack = _undoStack.Trim(undoStackSize + 1);

        if(reconcile)
            Reconcile(reset);
    }

    public void Redo()
    {
        if(_redoStack.TryPop(out var redoStack))
        {
            _undoStack.Push(redoStack);
            SkeletonPosing.PoseInfo = redoStack.Info.Clone();
            ModelPosing.Transform = redoStack.ModelTransform;
        }
    }

    public void Undo()
    {
        if(_undoStack.TryPop(out var undoStack))
            _redoStack.Push(undoStack);

        if(_undoStack.TryPeek(out var applicable))
        {
            SkeletonPosing.PoseInfo = applicable.Info.Clone();
            ModelPosing.Transform = applicable.ModelTransform;
        }
    }

    public void Reset(bool generateSnapshot = true, bool reset = true)
    {
        SkeletonPosing.ResetPose();
        ModelPosing.ResetTransform();

        _redoStack.Clear();

        if(generateSnapshot)
            Snapshot(reset);
    }

    public void ToggleOverlay()
    {
        OverlayOpen = !OverlayOpen;
    }

    private void Reconcile(bool reset = true, bool generateSnapshot = true)
    {
        _framework.RunOnTick(() =>
        {
            var all = new PoseImporterOptions(new BoneFilter(_posingService), TransformComponents.All, true);
            var poseFile = GeneratePoseFile();
            if(reset)
            {
                Reset(generateSnapshot, false);
            }
            ImportPose(poseFile, options: all, generateSnapshot: false);
        }, delayTicks: 2);
    }

    private PoseFile GeneratePoseFile()
    {
        var poseFile = new PoseFile();
        SkeletonPosing.ExportSkeletonPose(poseFile);
        ModelPosing.ExportModelPose(poseFile);
        return poseFile;
    }

    internal record struct PoseStack(PoseInfo Info, Transform ModelTransform);
}

public enum ExpressionPhase
{
    None, One, Two, Three
}
