using System;
using System.Collections.Generic;
using System.Text;

namespace TinyNet
{
	class Proc : NetListener
	{
		public void OnReceive(NetHandler handler, ByteBuffer buffer)
		{
			Console.WriteLine("{0}", Encoding.UTF8.GetString(buffer.array, buffer.offset, buffer.length));
			buffer.Reset();
		}

		public void OnError(NetHandler handler, Exception error)
		{

		}

		public void OnClose(NetHandler handler)
		{

		}
	}

	class TestCase
	{
		private static NetManager mgr = null;
		public static void StartListen(int port)
		{
			if (mgr == null)
				mgr = new NetManager();
			mgr.Listen(port, delegate(NetHandler socket)
			{
				socket.Listen = new Proc();
			});
		}

		public static void StartConnect(string ipport)
		{
			if (mgr == null)
				mgr = new NetManager();
			NetHandler socket = mgr.Connect(ipport, 3000);
			socket.Listen = new Proc();
		}
	}
}
