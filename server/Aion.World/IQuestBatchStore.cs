using Aion.Application.Gameplay;

namespace Aion.World;

public interface IQuestBatchStore
{
    Task<IReadOnlyList<QuestRecord>> SaveQuestBatchAsync(Guid character, IReadOnlyList<QuestRecord> quests, CancellationToken ct);
}
