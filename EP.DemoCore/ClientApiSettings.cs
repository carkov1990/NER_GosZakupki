using PravoRu.Common.ClientApi;

namespace EP.Demo.Core
{
	public class ClientApiSettings : IClientApiSettings
	{
		public string BaseUri { get; } = "http://int-api.kad.local:8083/ArbitrCaseCard";
	}
}