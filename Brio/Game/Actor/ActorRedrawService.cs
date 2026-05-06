using Brio.Game.Actor.Extensions;
using Brio.Game.Core;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using System;
using System.Threading.Tasks;

namespace Brio.Game.Actor;

public class ActorRedrawService(IFramework framework, IObjectTable objectTable, IClientState clientState)
{
    public delegate void ActorRedrawEventDelegate(IGameObject go, RedrawStage stage);

    public event ActorRedrawEventDelegate? ActorRedrawEvent;
    
    public static bool SuspendRedraws { get; set; } = false;

    private readonly IFramework _framework = framework;

    private readonly IObjectTable _objectTable = objectTable;

    private readonly IClientState _clientState = clientState;

    public Task<RedrawResult> RedrawActor(int objectIndex)
    {
        if(SuspendRedraws) return Task.FromResult(RedrawResult.Failed);
        var actor = _objectTable[objectIndex];
        if(actor == null)
            return Task.FromResult(RedrawResult.Failed);

        return RedrawActor(actor);
    }

    public async Task<RedrawResult> RedrawActor(IGameObject go)
    {
        if(SuspendRedraws || !_clientState.IsLoggedIn) return RedrawResult.Failed;
        Brio.Log.Debug($"Beginning Brio redraw on gameobject {go.ObjectIndex}...");
        DisableDraw(go);
        try
        {
            ActorRedrawEvent?.Invoke(go, RedrawStage.After);
            await DrawWhenReady(go);
            await WaitForDrawing(go);
            ActorRedrawEvent?.Invoke(go, RedrawStage.After);
            Brio.Log.Debug($"Brio redraw complete on gameobject {go.ObjectIndex}.");
            return RedrawResult.Full;
        }
        catch(Exception e)
        {
            Brio.Log.Error(e, $"Brio redraw failed on gameobject {go.ObjectIndex}.");
            return RedrawResult.Failed;
        }
    }

    public unsafe void DisableDraw(IGameObject go)
    {
        if(SuspendRedraws || !_clientState.IsLoggedIn) return;
        if(go == null || !go.IsValid() || go.Address == 0) return;
        var native = go.Native();
        if(native != null) native->DisableDraw();
    }

    public unsafe void EnableDraw(IGameObject go)
    {
        if(SuspendRedraws || !_clientState.IsLoggedIn) return;
        if(go == null || !go.IsValid() || go.Address == 0) return;
        var native = go.Native();
        if(native != null) native->EnableDraw();
    }

    public unsafe Task DrawWhenReady(IGameObject go)
    {
        return _framework.RunUntilSatisfied(
           () => 
           {
               if(SuspendRedraws || !_clientState.IsLoggedIn) return true;
               if(go == null || !go.IsValid() || go.Address == 0) return true;
               return go.Native()->IsReadyToDraw();
           },
           (_) => EnableDraw(go),
           100,
           dontStartFor: 2
       );
    }

    public unsafe Task WaitForDrawing(IGameObject go)
    {
        return _framework.RunUntilSatisfied(
           () =>
           {
               if(SuspendRedraws || !_clientState.IsLoggedIn) return true;
               if(go == null || !go.IsValid() || go.Address == 0) return true;
               var drawObject = go.Native()->DrawObject;
               if(drawObject == null)
                   return false;

               return drawObject->IsVisible;
           },
           (_) => { },
           100,
           dontStartFor: 2
           );
    }

    public enum RedrawResult
    {
        NoChange,
        Optmized,
        Full,
        Failed
    }

    public enum RedrawStage
    {
        Before,
        After
    }
}
