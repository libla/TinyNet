using System;

namespace TinyNet
{
	class TestCase
	{
		private static NetManager mgr = null;
		public static NetManager NetMgr
		{
			get
			{
				if (mgr == null)
				{
					Settings settings = new Settings
					{
						events =
						{
							request = (handler, request) =>
							{
								UTF8StringRequest req = request as UTF8StringRequest;
								if (req != null)
								{
									Console.WriteLine("{0}", req.Value);
								}
							}
						}
					};
					mgr = new NetManager<UTF8StringRequest>(settings);
				}
				return mgr;
			}
		}

		public static void StartListen(int port)
		{
			NetMgr.Listen(port, delegate(NetHandler socket)
			{
				Console.WriteLine("New Connection");
			});
		}

		public static void StartConnect(string ipport)
		{
			NetMgr.Connect(ipport, 3000, (handler, exception) =>
			{
				if (exception == null)
				{
					Console.WriteLine("Connected");
					UTF8StringRequest req = new UTF8StringRequest { Value = "Hello, world!" };
					req.Send(handler);
				}
				else
				{
					Console.WriteLine("Connnect Fail! {0}", exception.ToString());
				}
			});
		}
	}
}
