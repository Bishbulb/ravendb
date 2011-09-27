﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Raven.Abstractions.Data;
using Raven.Database.Storage;
using Raven.Database.Tasks;

namespace Raven.Database.Indexing
{
	public class ReducingExecuter : AbstractIndexingExecuter
	{
		public ReducingExecuter(ITransactionalStorage transactionalStorage, WorkContext context, TaskScheduler scheduler)
			: base(transactionalStorage, context, scheduler)
		{
		}

		protected void HandleReduceForIndex(IndexToWorkOn indexToWorkOn)
		{
			List<MappedResultInfo> reduceKeyAndEtags = null;
			try
			{
				transactionalStorage.Batch(actions =>
				{
					reduceKeyAndEtags = actions.MappedResults.GetMappedResultsReduceKeysAfter
						(
							indexToWorkOn.IndexName,
							indexToWorkOn.LastIndexedEtag,
							loadData: false
						).ToList();

					if(log.IsDebugEnabled)
					{
						if (reduceKeyAndEtags.Count > 0)
							log.Debug(() => string.Format("Found {0} mapped results for keys [{1}] for index {2}", reduceKeyAndEtags.Count, string.Join(", ", reduceKeyAndEtags.Select(x => x.ReduceKey).Distinct()), indexToWorkOn.IndexName));
						else
							log.Debug("No reduce keys found for {0}", indexToWorkOn.IndexName);
					}

					var reduceKeyAndGroupIds =
						reduceKeyAndEtags.Select(x => new ReduceKeyAndGroupId {ReduceKey = x.ReduceKey, ReduceGroupId = x.ReduceGroupId}).
							Distinct().ToArray();
					new ReduceTask
					{
						Index = indexToWorkOn.IndexName,
						ReduceKeys = reduceKeyAndGroupIds,
					}.Execute(context);
				});
			}
			finally
			{
				if (reduceKeyAndEtags != null && reduceKeyAndEtags.Count > 0)
				{
					var lastByEtag = GetLastByEtag(reduceKeyAndEtags);
					var lastEtag = lastByEtag.Etag;

					var lastIndexedEtag = new ComparableByteArray(lastEtag.ToByteArray());
					// whatever we succeeded in indexing or not, we have to update this
					// because otherwise we keep trying to re-index failed mapped results
					transactionalStorage.Batch(actions =>
					{
						if (new ComparableByteArray(indexToWorkOn.LastIndexedEtag.ToByteArray()).CompareTo(lastIndexedEtag) <= 0)
						{
							actions.Indexing.UpdateLastReduced(indexToWorkOn.IndexName, lastByEtag.Etag, lastByEtag.Timestamp);
						}
					});
				}
			}
		}


		private MappedResultInfo GetLastByEtag(List<MappedResultInfo> reduceKeyAndEtags)
		{
			// the last item is either the first or the last

			var first = reduceKeyAndEtags.First();
			var last = reduceKeyAndEtags.Last();

			if (new ComparableByteArray(first.Etag.ToByteArray()).CompareTo(new ComparableByteArray(last.Etag.ToByteArray())) < 0)
				return last;
			return first;
		}

		protected override bool IsIndexStale(IndexStats indexesStat, IStorageActionsAccessor actions)
		{
			return actions.Staleness.IsReduceStale(indexesStat.Name);
		}

		protected override void FlushAllIndexes()
		{
			context.IndexStorage.FlushReduceIndexes();
		}

		protected override IndexToWorkOn GetIndexToWorkOn(IndexStats indexesStat)
		{
			return new IndexToWorkOn
			{
				IndexName = indexesStat.Name,
				LastIndexedEtag = indexesStat.LastReducedEtag ?? Guid.Empty
			};
		}


		protected override void ExecuteIndexingWorkOnMultipleThreads(IEnumerable<IndexToWorkOn> indexesToWorkOn)
		{
			Parallel.ForEach(indexesToWorkOn, new ParallelOptions
			{
				MaxDegreeOfParallelism = context.Configuration.MaxNumberOfParallelIndexTasks,
				TaskScheduler = scheduler
			}, HandleReduceForIndex);
		}

		protected override void ExecuteIndexingWorkOnSingleThread(IEnumerable<IndexToWorkOn> indexesToWorkOn)
		{
			foreach (var indexToWorkOn in indexesToWorkOn)
			{
				HandleReduceForIndex(indexToWorkOn);
			}
		}

		protected override bool IsValidIndex(IndexStats indexesStat)
		{
			var indexDefinition = context.IndexDefinitionStorage.GetIndexDefinition(indexesStat.Name);
			return indexDefinition != null && indexDefinition.IsMapReduce;
		}
	}
}