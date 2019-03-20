using System.Net.Http;

namespace EP.Demo.Core
{
	public class HttpClientFactory : IHttpClientFactory
	{
		public HttpClient CreateClient(string name)
		{
			return new HttpClient();
		}
	}
}