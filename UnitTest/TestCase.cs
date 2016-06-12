using System;
using UnityEngine;

namespace TinyNet
{
	public class TestCase
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
									Debug.Log(req.Value);
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
				Debug.Log("New Connection");
			});
		}

		public static void StartConnect(string ipport)
		{
			NetMgr.Connect(ipport, 3000, (handler, exception) =>
			{
				if (exception == null)
				{
					Debug.Log("Connected");
					UTF8StringRequest req = new UTF8StringRequest { Value = "Hello, world!" };
					req.Send(handler);
				}
				else
				{
					Debug.Log("Connnect Fail! " + exception.ToString());
				}
			});
		}

		public static void Main(string[] args)
		{
			NetManager.Initialize();
			StartListen(12306);
			StartConnect("localhost:12306");
		}
	}
}
