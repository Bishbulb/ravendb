using Raven.Abstractions.Data;
using Raven.Database.Plugins;
using Raven.Json.Linq;

namespace Raven.Bundles.Quotas.Documents.Triggers
{
	public class DocumentCountQuotaForDocumentsPutTrigger : AbstractPutTrigger
	{
		public override VetoResult AllowPut(string key, RavenJObject document, RavenJObject metadata,
		                                    TransactionInformation transactionInformation)
		{
			return DocQuotaConfiguration.GetConfiguration(Database).AllowPut();
		}

	}
}