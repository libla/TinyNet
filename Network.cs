using System;
using System.Threading;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace TinyNet
{
	#region 发送和接收缓冲区
	/// <summary>
	/// 填充数据包
	/// </summary>
	public struct BufferSegment
	{
		public byte[] Array;
		public int Length;
		public int Offset;
		public IntPtr IntPtr;
		public byte Byte;

		public static BufferSegment Make(byte[] array)
		{
			return Make(array, 0, array.Length);
		}

		public static BufferSegment Make(byte[] array, int length)
		{
			return Make(array, 0, length);
		}

		public static BufferSegment Make(byte[] array, int offset, int length)
		{
			BufferSegment seg = new BufferSegment
			{
				Array = array,
				Length = length,
				Offset = offset,
			};
			return seg;
		}

		public static BufferSegment Make(IntPtr intptr, int length)
		{
			BufferSegment seg = new BufferSegment
			{
				Array = null,
				IntPtr = intptr,
				Length = length,
			};
			return seg;
		}

		public static BufferSegment Make(byte onebyte)
		{
			BufferSegment seg = new BufferSegment
			{
				Array = null,
				IntPtr = IntPtr.Zero,
				Byte = onebyte,
			};
			return seg;
		}
	}

	/// <summary>
	/// 复用缓冲区
	/// </summary>
	public class ByteBuffer
	{
		private byte[] _array;
		public byte[] array
		{
			get { return _array; }
		}
		private int _length;
		public int length
		{
			get { return _length; }
		}
		private int _offset;
		public int offset
		{
			get { return _offset; }
		}

		public ByteBuffer()
		{
			_array = new byte[1024];
			_length = 0;
			_offset = 0;
		}

		private static readonly Stack<ByteBuffer> freelist = new Stack<ByteBuffer>();

		public static ByteBuffer New()
		{
			if (freelist.Count > 0)
			{
				lock (freelist)
				{
					if (freelist.Count > 0)
						return freelist.Pop();
				}
			}
			return new ByteBuffer();
		}

		public void Release()
		{
			Reset();
			lock (freelist)
			{
				freelist.Push(this);
			}
		}

		public void Write(BufferSegment seg)
		{
			if (seg.Array != null)
				Write(seg.Array, seg.Offset, seg.Length);
			else if (seg.IntPtr != IntPtr.Zero)
				Write(seg.IntPtr, seg.Length);
			else
				Write(seg.Byte);
		}

		private void checksize(int length)
		{
			if (_offset + _length + length > _array.Length)
			{
				int need = _length + length;
				if (need <= _array.Length && _offset >= (_array.Length + 1) >> 1)
				{
					Array.Copy(_array, _offset, _array, 0, _length);
					_offset = 0;
				}
				else
				{
					int size = _array.Length << 1;
					while (size < need)
					{
						size = size << 1;
					}
					byte[] array = new byte[size];
					Array.Copy(_array, _offset, array, 0, _length);
					_offset = 0;
					_array = array;
				}
			}
		}

		public void Write(byte[] bytes, int offset, int length)
		{
			checksize(length);
			Array.Copy(bytes, offset, _array, _offset + _length, length);
			_length += length;
		}

		public void Write(IntPtr bytes, int length)
		{
			checksize(length);
			Marshal.Copy(bytes, _array, _offset + _length, length);
			_length += length;
		}

		public void Write(byte onebyte)
		{
			checksize(1);
			_array[_offset + _length] = onebyte;
			_length += 1;
		}

		public void Pop(int length)
		{
			if (length > _length)
				length = _length;
			_offset += length;
			_length -= length;
			if (_length == 0)
			{
				Reset();
			}
		}

		public void Reset()
		{
			_offset = 0;
			_length = 0;
		}
	}
	#endregion

	#region 解析网络请求
	/// <summary>
	/// 基础请求类
	/// </summary>
	public abstract class Request
	{
		private readonly ByteBuffer buffer;

		protected Request()
		{
			buffer = ByteBuffer.New();
		}

		~Request()
		{
			buffer.Release();
		}

		internal bool Input(byte[] bytes, int offset, ref int length)
		{
			int len = length;
			while (len > 0)
			{
				int l = PreTest(buffer, bytes, offset, length);
				offset += l;
				len -= l;
				if (Test(buffer))
				{
					length -= len;
					return true;
				}
			}
			return false;
		}

		protected abstract int PreTest(ByteBuffer buffer, byte[] bytes, int offset, int length);
		protected abstract bool Test(ByteBuffer buffer);
		internal abstract void Execute(NetHandler handler);
		public abstract bool Send(NetHandler handler);

		public virtual void Reset()
		{
			buffer.Reset();
		}
	}

	/// <summary>
	/// 无ID分组
	/// </summary>
	public abstract class Request<T> : Request
	{
		public T Value;
		internal override void Execute(NetHandler handler)
		{
			Handle(handler, Value);
		}

		protected virtual void Handle(NetHandler handler, T t)
		{
			if (DefaultHandler != null)
				DefaultHandler(handler, t);
		}

		public static event Action<NetHandler, T> DefaultHandler;
	}

	/// <summary>
	/// 按ID分组
	/// </summary>
	public abstract class Request<TKey, T> : Request
	{
		public TKey ID;
		public T Value;
		internal override void Execute(NetHandler handler)
		{
			Handle(handler, ID, Value);
		}

		protected virtual void Handle(NetHandler handler, TKey k, T t)
		{
			Action<NetHandler, T> action;
			if (_Handlers.TryGetValue(k, out action))
			{
				action(handler, t);
			}
			else
			{
				if (DefaultHandler != null)
					DefaultHandler(handler, k, t);
			}
		}

		private static readonly Dictionary<TKey, Action<NetHandler, T>> _Handlers = new Dictionary<TKey, Action<NetHandler, T>>();
		public static Dictionary<TKey, Action<NetHandler, T>> Handlers
		{
			get
			{
				return _Handlers;
			}
		}
		public static event Action<NetHandler, TKey, T> DefaultHandler;
	}
	#endregion

	#region 设置相关
	public class Settings
	{
		public uint timeout;
		public int capacity;
		public int worklimit;
		public Events events;

		public struct Events
		{
			public Action<NetHandler, Request> request;
			public Action<NetHandler, int> read;
			public Action<NetHandler, int> write;
			public Action<NetHandler> close;
			public Action<NetHandler, Exception> exception;
		}

		public Settings()
		{
			timeout = 5;
			capacity = 2 << 24;
			worklimit = 4;
		}
	}
	#endregion

	/// <summary>
	/// 网络连接抽象句柄
	/// </summary>
	public abstract class NetHandler
	{
		public abstract bool Send(BufferSegment seg);
		public abstract bool Send(BufferSegment seg1, BufferSegment seg2);
		public abstract bool Send(BufferSegment seg1, BufferSegment seg2, BufferSegment seg3);
		public abstract bool Send(BufferSegment seg, params BufferSegment[] segs);
		public abstract void Close();
		public abstract bool Connected { get; }
		public abstract IPEndPoint Remote { get; }
		public abstract IPEndPoint Local { get; }
		public abstract short TTL { get; set; }
		public abstract bool NoDelay { get; set; }
	}

	/// <summary>
	/// 网络管理全局接口
	/// </summary>
	public abstract class NetManager : IDisposable
	{
		protected readonly Settings settings = null;
		private bool running = true;
		private int workcount = 0;
		private readonly Queue<NetHandlerImpl> workqueue = new Queue<NetHandlerImpl>();
		private readonly Dictionary<NetHandlerImpl, bool> workindex = new Dictionary<NetHandlerImpl, bool>();
		private readonly Dictionary<int, Control> listens = new Dictionary<int, Control>();
		private readonly Dictionary<NetHandlerImpl, bool> sockets = new Dictionary<NetHandlerImpl, bool>();

		private static readonly Dictionary<NetManager, bool> managers = new Dictionary<NetManager, bool>();

		#region 内部使用的类
		private class Control
		{
			public bool running;
			public Action<NetHandler> action;
		}
		#endregion

		protected NetManager(Settings settings)
		{
			this.settings = settings;
			lock (managers)
			{
				managers.Add(this, true);
			}
		}

		public static void Initialize()
		{
			Loop.Initialize();
		}

		public void Listen(int port, Action<NetHandler> callback)
		{
			if (!running)
				throw new ObjectDisposedException(ToString());
			new Thread(delegate ()
			{
				Thread.CurrentThread.Name = "Network Server";
				TcpListener server = new TcpListener(IPAddress.Any, port);
				server.Start();
				Control ctrl;
				lock (listens)
				{
					if (listens.TryGetValue(port, out ctrl))
					{
						ctrl.running = true;
					}
					else
					{
						ctrl = new Control { running = true };
						listens[port] = ctrl;
					}
				}
				ctrl.action = callback;
				while (running)
				{
					if (!server.Server.Poll(1000, SelectMode.SelectRead))
					{
						if (!ctrl.running)
							break;
						continue;
					}
					if (!server.Pending())
						break;
					NetHandlerImpl socket = new NetHandlerImpl(server.AcceptSocket(), this);
					socket.Socket.Blocking = false;
					socket.OnConnected();
					Loop.Run(() =>
					{
						ctrl.action(socket);
					});
				}
				lock (listens)
				{
					Control ctrlnow;
					if (listens.TryGetValue(port, out ctrlnow) && ctrlnow == ctrl)
					{
						listens.Remove(port);
					}
				}
				server.Stop();
			}).Start();
		}

		public void Stop(int port)
		{
			if (!running)
				throw new ObjectDisposedException(ToString());
			lock (listens)
			{
				Control ctrl;
				if (listens.TryGetValue(port, out ctrl))
				{
					ctrl.running = false;
					listens.Remove(port);
				}
			}
		}

		public NetHandler Connect(string hostport, int timeout)
		{
			Regex regex = new Regex("^(?<host>.+):(?<port>\\d+)$", RegexOptions.Multiline);
			Match match = regex.Match(hostport);
			if (!match.Success)
				throw new ArgumentException();
			return Connect(match.Groups["host"].Captures[0].Value, Convert.ToInt32(match.Groups["port"].Captures[0].Value), timeout);
		}

		public void Connect(string hostport, int timeout, Action<NetHandler, Exception> callback)
		{
			Regex regex = new Regex("^(?<host>.+):(?<port>\\d+)$", RegexOptions.Multiline);
			Match match = regex.Match(hostport);
			if (match.Success)
			{
				Connect(match.Groups["host"].Captures[0].Value, Convert.ToInt32(match.Groups["port"].Captures[0].Value), timeout, callback);
			}
			else
			{
				Exception e = new ArgumentException();
				Loop.RunDelay(() =>
				{
					callback(null, e);
				});
			}
		}

		public NetHandler Connect(string host, int port, int timeout)
		{
			AutoResetEvent reset = new AutoResetEvent(false);
			NetHandler socket = null;
			Exception e = null;
			ConnectImpl(host, port, timeout, (handler, exception) =>
			{
				socket = handler;
				e = exception;
				reset.Set();
			});
			reset.WaitOne();
			reset.Close();
			if (e != null)
				throw e;
			return socket;
		}

		public void Connect(string host, int port, int timeout, Action<NetHandler, Exception> callback)
		{
			ConnectImpl(host, port, timeout, (handler, exception) =>
			{
				Loop.Run(() =>
				{
					callback(handler, exception);
				});
			});
		}

		public void Dispose()
		{
			lock (managers)
			{
				if (!managers.Remove(this))
					return;
			}
			running = false;
			lock (workqueue)
			{
				Monitor.PulseAll(workqueue);
			}
			List<NetHandlerImpl> keys;
			lock (sockets)
			{
				keys = new List<NetHandlerImpl>(sockets.Count);
				foreach (var kv in sockets)
				{
					keys.Add(kv.Key);
				}
			}
			foreach (var handler in keys)
			{
				handler.Close();
			}
		}

		public void Close()
		{
			Dispose();
		}

		#region 连接
		private void ConnectImpl(string host, int port, int timeout, Action<NetHandler, Exception> callback)
		{
			if (!running)
				throw new ObjectDisposedException(ToString());
			Stopwatch clock = new Stopwatch();
			clock.Start();
			Dns.BeginGetHostAddresses(host, ar =>
			{
				clock.Stop();
				timeout -= (int)clock.ElapsedMilliseconds;
				try
				{
					IPAddress[] iplist = Dns.EndGetHostAddresses(ar);
					if (timeout > 0)
					{
						AddressFamily family = AddressFamily.InterNetwork;
						for (int i = 0; i < iplist.Length; ++i)
						{
							if (iplist[i].AddressFamily == AddressFamily.InterNetworkV6)
							{
								family = AddressFamily.InterNetworkV6;
								break;
							}
						}
						NetHandlerImpl socket = new NetHandlerImpl(family, this);
						Timer timer = new Timer(state =>
						{
							socket.Socket.Close();
						}, null, Timeout.Infinite, Timeout.Infinite);
						socket.Socket.BeginConnect(iplist, port, result =>
						{
							try
							{
								timer.Dispose();
							}
							catch
							{
							}
							Exception exception = null;
							try
							{
								socket.Socket.EndConnect(result);
								socket.Socket.Blocking = false;
								socket.OnConnected();
							}
							catch (ObjectDisposedException)
							{
								exception = new SocketException((int)SocketError.TimedOut);
							}
							catch (Exception e)
							{
								exception = e;
								socket.Socket.Close();
							}
							callback(exception == null ? socket : null, exception);
						}, null);
						timer.Change(timeout, Timeout.Infinite);
					}
					else
					{
						callback(null, new SocketException((int)SocketError.TimedOut));
					}
				}
				catch (Exception e)
				{
					callback(null, e);
				}
			}, null);
		}
		#endregion

		private void SendQueue(NetHandlerImpl handler)
		{
			lock (workqueue)
			{
				if (!workindex.ContainsKey(handler))
				{
					workindex.Add(handler, true);
					workqueue.Enqueue(handler);
					Monitor.Pulse(workqueue);
				}
			}
			if (workcount >= settings.worklimit)
				return;
			if (Interlocked.Increment(ref workcount) > settings.worklimit)
			{
				Interlocked.Decrement(ref workcount);
				return;
			}
			new Thread(delegate ()
			{
				Thread.CurrentThread.Name = "Network Write";
				while (running)
				{
					handler = null;
					lock (workqueue)
					{
						while (running)
						{
							if (workqueue.Count != 0)
							{
								handler = workqueue.Dequeue();
								workindex.Remove(handler);
								break;
							}
							Monitor.Wait(workqueue);
						}
					}
					if (handler != null)
					{
						bool result;
						try
						{
							result = handler.Socket.Poll((int)settings.timeout * 1000, SelectMode.SelectWrite);
						}
						catch (ObjectDisposedException)
						{
							continue;
						}
						if (result)
						{
							if (!handler.SendReady())
								continue;
						}
						lock (workqueue)
						{
							if (!workindex.ContainsKey(handler))
							{
								workindex.Add(handler, true);
								workqueue.Enqueue(handler);
							}
						}
					}
				}
			}).Start();
		}

		#region 网络连接的具体类
		private class NetHandlerImpl : NetHandler
		{
			public readonly Socket Socket;

			public readonly ByteBuffer buffer = new ByteBuffer();
			private readonly NetManager manager;
			private Request request;

			public NetHandlerImpl(AddressFamily family, NetManager manager)
			{
				this.manager = manager;
				Socket = new Socket(family, SocketType.Stream, ProtocolType.Tcp);
				Socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Linger, new LingerOption(false, 0));
			}

			public NetHandlerImpl(Socket socket, NetManager manager)
			{
				this.manager = manager;
				Socket = socket;
				Socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Linger, new LingerOption(false, 0));
			}

			public void OnConnected()
			{
				lock (manager.sockets)
				{
					manager.sockets.Add(this, true);
				}
				new Thread(delegate ()
				{
					Thread.CurrentThread.Name = "Network Read";
					byte[] bytes = new byte[64 * 1024];
					while (manager.running)
					{
						bool result;
						try
						{
							result = Socket.Poll(-1, SelectMode.SelectRead);
						}
						catch (ObjectDisposedException)
						{
							lock (manager.sockets)
							{
								manager.sockets.Remove(this);
							}
							break;
						}
						if (result)
						{
							int total = 0;
							try
							{
								while (Socket.Available > 0)
								{
									int length = Socket.Receive(bytes);
									if (length == 0)
									{
										break;
									}
									total += length;
									OnReceive(bytes, length);
								}
								if (total == 0)
								{
									Close();
									break;
								}
							}
							catch (SocketException e)
							{
								if (e.SocketErrorCode == SocketError.ConnectionReset ||
									e.SocketErrorCode == SocketError.ConnectionAborted ||
									e.SocketErrorCode == SocketError.NotConnected ||
									e.SocketErrorCode == SocketError.Shutdown)
								{
									Close();
									break;
								}
								if (manager.settings.events.exception != null)
								{
									Loop.Run(() =>
									{
										if (manager.settings.events.exception != null)
											manager.settings.events.exception(this, e);
									});
								}
							}
							catch (ObjectDisposedException)
							{
								lock (manager.sockets)
								{
									manager.sockets.Remove(this);
								}
								break;
							}
							catch (Exception e)
							{
								if (manager.settings.events.exception != null)
								{
									Loop.Run(() =>
									{
										if (manager.settings.events.exception != null)
											manager.settings.events.exception(this, e);
									});
								}
							}
							if (total != 0 && manager.settings.events.read != null)
							{
								Loop.Run(() =>
								{
									if (manager.settings.events.read != null)
										manager.settings.events.read(this, total);
								});
							}
						}
					}
				}).Start();
			}

			public override void Close()
			{
				Socket.Close();
				lock (manager.sockets)
				{
					manager.sockets.Remove(this);
				}
				if (manager.settings.events.close != null)
				{
					Loop.Run(() =>
					{
						if (manager.settings.events.close != null)
						{
							manager.settings.events.close(this);
							manager.settings.events.close = null;
						}
					});
				}
			}

			public override bool Send(BufferSegment seg)
			{
				lock (buffer)
				{
					if (buffer.length >= manager.settings.capacity)
						return false;
					buffer.Write(seg);
				}
				manager.SendQueue(this);
				return true;
			}

			public override bool Send(BufferSegment seg1, BufferSegment seg2)
			{
				lock (buffer)
				{
					if (buffer.length >= manager.settings.capacity)
						return false;
					buffer.Write(seg1);
					buffer.Write(seg2);
				}
				manager.SendQueue(this);
				return true;
			}

			public override bool Send(BufferSegment seg1, BufferSegment seg2, BufferSegment seg3)
			{
				lock (buffer)
				{
					if (buffer.length >= manager.settings.capacity)
						return false;
					buffer.Write(seg1);
					buffer.Write(seg2);
					buffer.Write(seg3);
				}
				manager.SendQueue(this);
				return true;
			}

			public override bool Send(BufferSegment seg, params BufferSegment[] segs)
			{
				lock (buffer)
				{
					if (buffer.length >= manager.settings.capacity)
						return false;
					buffer.Write(seg);
					for (int i = 0; i < segs.Length; ++i)
					{
						buffer.Write(segs[i]);
					}
				}
				manager.SendQueue(this);
				return true;
			}

			public override bool Connected
			{
				get { return Socket.Connected; }
			}

			public override IPEndPoint Remote
			{
				get
				{
					return Socket.RemoteEndPoint as IPEndPoint;
				}
			}

			public override IPEndPoint Local
			{
				get
				{
					return Socket.LocalEndPoint as IPEndPoint;
				}
			}

			public override short TTL
			{
				get { return Socket.Ttl; }
				set { Socket.Ttl = value; }
			}

			public override bool NoDelay
			{
				get { return !Socket.NoDelay; }
				set { Socket.NoDelay = !value; }
			}

			public bool SendReady()
			{
				int length = 0;
				bool needsend = false;
				bool sended = false;
				lock (buffer)
				{
					if (buffer.length != 0)
					{
						try
						{
							length = Socket.Send(buffer.array, buffer.offset, buffer.length, SocketFlags.None);
						}
						catch (ObjectDisposedException)
						{
							return false;
						}
						sended = true;
						buffer.Pop(length);
						needsend = buffer.length != 0;
					}
				}
				if (sended && manager.settings.events.write != null)
				{
					Loop.Run(() =>
					{
						if (manager.settings.events.write != null)
							manager.settings.events.write(this, length);
					});
				}
				return needsend;
			}

			private void OnReceive(byte[] bytes, int length)
			{
				int offset = 0;
				int size = length - offset;
				if (request == null)
					request = manager.NewRequest();
				try
				{
					while (size > 0 && request.Input(bytes, offset, ref size))
					{
						offset += size;
						size = length - offset;
						Request old = request;
						request = manager.NewRequest();
						Loop.Run(delegate ()
						{
							if (manager.settings.events.request != null)
							{
								manager.settings.events.request(this, old);
							}
							old.Execute(this);
							manager.ReleaseRequest(old);
						});
					}
				}
				catch (Exception e)
				{
					Socket.Close();
					if (manager.settings.events.close != null)
					{
						Loop.Run(delegate ()
						{
							if (manager.settings.events.close != null)
								manager.settings.events.close(this);
						});
					}
					if (manager.settings.events.exception != null)
					{
						Loop.Run(delegate ()
						{
							if (manager.settings.events.exception != null)
								manager.settings.events.exception(this, e);
						});
					}
				}
			}
		}

		protected abstract Request NewRequest();
		protected abstract void ReleaseRequest(Request request);
		#endregion

		#region 主循环调用
		private static class Loop
		{
			private static bool initialized = false;
			private static readonly object actions_mtx = new object();
			private static List<Action> actions = new List<Action>();
			private static List<Action> actions_tmp = new List<Action>();
			private static readonly List<Action> actions_delay = new List<Action>();
			private static readonly List<Action> actions_delay_tmp = new List<Action>();
			private static readonly List<Action> actions_delay_async = new List<Action>();
			private static bool threadInit = false;
			private static int threadID = 0;

			public static void Initialize()
			{
				if (!initialized)
				{
					if (!Application.isPlaying)
						return;
					initialized = true;
					GameObject go = new GameObject("Network");
					UnityObject.DontDestroyOnLoad(go);
					go.hideFlags |= HideFlags.HideInHierarchy;
					go.AddComponent<Updater>();
				}
			}

			public static void Run(Action action)
			{
				if (threadInit && threadID == Thread.CurrentThread.ManagedThreadId)
				{
					action();
				}
				else
				{
					lock (actions_mtx)
					{
						actions.Add(action);
					}
				}
			}

			public static void RunDelay(Action action)
			{
				if (threadInit && threadID == Thread.CurrentThread.ManagedThreadId)
				{
					actions_delay.Add(action);
				}
				else
				{
					lock (actions_delay_async)
					{
						actions_delay_async.Add(action);
					}
				}
			}

			private class Updater : MonoBehaviour
			{
				void Start()
				{
					threadID = Thread.CurrentThread.ManagedThreadId;
					threadInit = true;
				}

				void Update()
				{
					if (actions.Count > 0)
					{
						lock (actions_mtx)
						{
							var tmp = actions_tmp;
							actions_tmp = actions;
							actions = tmp;
						}
						for (int i = 0, j = actions_tmp.Count; i < j; ++i)
						{
							try
							{
								actions_tmp[i]();
							}
							catch (Exception)
							{
							}
						}
						actions_tmp.Clear();
					}
					for (int i = 0; i < actions_delay.Count; i++)
						actions_delay_tmp.Add(actions_delay[i]);
					actions_delay.Clear();
					if (actions_delay_async.Count > 0)
					{
						lock (actions_delay_async)
						{
							for (int i = 0; i < actions_delay_async.Count; i++)
								actions_delay_tmp.Add(actions_delay_async[i]);
							actions_delay_async.Clear();
						}
					}
					for (int i = 0; i < actions_delay_tmp.Count; i++)
					{
						try
						{
							actions_delay_tmp[i]();
						}
						catch (Exception)
						{
						}
					}
					actions_delay_tmp.Clear();
				}

				void OnApplicationQuit()
				{
					List<NetManager> keys;
					lock (managers)
					{
						keys = new List<NetManager>(managers.Count);
						foreach (var kv in managers)
						{
							keys.Add(kv.Key);
						}
					}
					foreach (var manager in keys)
					{
						manager.Dispose();
					}
				}
			}
		}
		#endregion
	}

	public sealed class NetManager<T> : NetManager
		where T : Request, new()
	{
		public NetManager()
			: base(new Settings()) { }
		public NetManager(Settings settings)
			: base(settings) { }

		protected override Request NewRequest()
		{
			return new T();
		}

		protected override void ReleaseRequest(Request request)
		{
			request.Reset();
		}
	}

	public class UTF8StringRequest : Request<string>
	{
		protected override int PreTest(ByteBuffer buffer, byte[] bytes, int offset, int length)
		{
			int len = length;
			for (int i = offset; i < length; ++i)
			{
				if (bytes[i] == 0)
				{
					len = i - offset + 1;
					break;
				}
			}
			buffer.Write(bytes, offset, len);
			return len;
		}

		protected override bool Test(ByteBuffer buffer)
		{
			if (buffer.array[buffer.offset + buffer.length - 1] == 0)
			{
				Value = System.Text.Encoding.UTF8.GetString(buffer.array, buffer.offset, buffer.length - 1);
				buffer.Reset();
				return true;
			}
			return false;
		}

		public override bool Send(NetHandler handler)
		{
			if (!handler.Send(BufferSegment.Make(System.Text.Encoding.UTF8.GetBytes(Value)), BufferSegment.Make(0)))
				return false;
			return true;
		}

		public override void Reset()
		{
			base.Reset();
			Value = null;
		}
	}
}