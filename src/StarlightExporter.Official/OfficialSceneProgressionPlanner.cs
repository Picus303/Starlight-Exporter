using Starlight.Protobuf.Core;
using Starlight.Protocol;

namespace StarlightExporter.Official;

public sealed class OfficialSceneProgressionPlanner
{
    private uint _enterSceneToken;
    private bool _readyAccepted;
    private bool _doneAccepted;

    public IReadOnlyList<IMessage> Observe(IMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        switch (message)
        {
            case PlayerEnterSceneNotify entered when entered.EnterSceneToken != 0:
                _enterSceneToken = entered.EnterSceneToken;
                _readyAccepted = false;
                _doneAccepted = false;
                return [new EnterSceneReadyReq { EnterSceneToken = _enterSceneToken }];

            case EnterSceneReadyRsp ready when ready.EnterSceneToken == _enterSceneToken
                && _enterSceneToken != 0:
                if (ready.Retcode != 0)
                {
                    throw SceneFailure("EnterSceneReady was rejected.", ready.Retcode);
                }
                _readyAccepted = true;
                return [
                    new SceneInitFinishReq { EnterSceneToken = _enterSceneToken },
                    new EnterSceneDoneReq { EnterSceneToken = _enterSceneToken },
                ];

            case SceneInitFinishRsp initialized when initialized.EnterSceneToken == _enterSceneToken:
                if (initialized.Retcode != 0)
                {
                    throw SceneFailure("SceneInitFinish was rejected.", initialized.Retcode);
                }
                break;

            case EnterSceneDoneRsp done when done.EnterSceneToken == _enterSceneToken:
                if (done.Retcode != 0)
                {
                    throw SceneFailure("EnterSceneDone was rejected.", done.Retcode);
                }
                _doneAccepted = true;
                break;
        }
        return [];
    }

    public PostEnterSceneReq CreatePostRequest(double totalTickTime)
    {
        if (!_readyAccepted || !_doneAccepted || _enterSceneToken == 0
            || !double.IsFinite(totalTickTime) || totalTickTime < 0)
        {
            throw new InvalidOperationException(
                "PostEnterScene is unavailable before an accepted scene progression.");
        }
        return new PostEnterSceneReq
        {
            EnterSceneToken = _enterSceneToken,
            TotalTickTime = totalTickTime,
        };
    }

    private static OfficialConnectivityException SceneFailure(string message, int retcode) =>
        new(OfficialConnectivityError.SyncIncomplete, message, retcode: retcode);
}
