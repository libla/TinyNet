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
		public int sendbytes;
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
			sendbytes = 2 << 24;
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
		private static bool IsRunning = true;

		private bool inited = false;
		private bool valid = true;
		private readonly List<NetHandlerImpl> sockets = new List<NetHandlerImpl>();
		private readonly List<NetHandlerImpl> newsockets = new List<NetHandlerImpl>();
		private readonly List<NetHandlerImpl> deletesockets = new List<NetHandlerImpl>();
		private readonly Dictionary<int, Control> listens = new Dictionary<int, Control>();

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
		}

		public static void Initialize()
		{
			Loop.Initialize();
		}

		public void Listen(int port, Action<NetHandler> callback)
		{
			if (!valid)
				throw new ObjectDisposedException(ToString());
			Init();
			new Thread(delegate()
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
				while (valid && IsRunning)
				{
					if (!server.Server.Poll(1000, SelectMode.SelectRead))
					{
						if (!ctrl.running)
							break;
						continue;
					}
					if (!server.Pending())
						break;
					NetHandlerImpl socket = new NetHandlerImpl(server.AcceptSocket()) { Manager = this };
					socket.Socket.Blocking = false;
					Loop.Run(delegate()
					{
						ctrl.action(socket);
					});
					lock (newsockets)
					{
						newsockets.Add(socket);
					}
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
			if (!valid)
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
			return !match.Success ? null :
				Connect(match.Groups["host"].Captures[0].Value, Convert.ToInt32(match.Groups["port"].Captures[0].Value), timeout);
		}

		public void Connect(string hostport, int timeout, Action<NetHandler, Exception> callback)
		{
			Regex regex = new Regex("^(?<host>.+):(?<port>\\d+)$", RegexOptions.Multiline);
			Match match = regex.Match(hostport);
			if (match.Success)
				Connect(match.Groups["host"].Captures[0].Value, Convert.ToInt32(match.Groups["port"].Captures[0].Value), timeout, callback);
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
			valid = false;
		}

		public void Close()
		{
			Dispose();
		}

		#region 初始化
		private void Init()
		{
			if (inited)
				return;

			inited = true;

			new Thread(delegate()
			{
				Thread.CurrentThread.Name = "Network Task";
				List<Socket> reads = new List<Socket>();
				List<Socket> writers = new List<Socket>();
				List<Socket> errors = new List<Socket>();
				Dictionary<Socket, NetHandlerImpl> sockethandlers = new Dictionary<Socket, NetHandlerImpl>();

				byte[] bytes = new byte[64 * 1024];

				while (valid && IsRunning)
				{
					if (newsockets.Count > 0)
					{
						lock (newsockets)
						{
							sockets.AddRange(newsockets);
							newsockets.Clear();
						}
					}
					if (deletesockets.Count > 0)
					{
						lock (deletesockets)
						{
							for (int i = 0; i < deletesockets.Count; i++)
							{
								NetHandlerImpl socket = deletesockets[i];
								sockets.Remove(socket);
								socket.Socket.Close();
								if (settings.events.close != null)
								{
									Loop.Run(delegate()
									{
										if (settings.events.close != null)
											settings.events.close(socket);
									});
								}
							}
							deletesockets.Clear();
						}
					}
					if (sockets.Count == 0)
					{
						Thread.Sleep((int)settings.timeout);
						continue;
					}
					for (int i = 0; i < sockets.Count; i++)
					{
						NetHandlerImpl socket = sockets[i];
						sockethandlers.Add(socket.Socket, socket);
						reads.Add(socket.Socket);
						errors.Add(socket.Socket);
						if (socket.need_send)
							writers.Add(socket.Socket);
					}
					Socket.Select(reads, writers, errors, (int)settings.timeout);
					for (int i = 0; i < writers.Count; i++)
					{
						NetHandlerImpl socket = sockethandlers[writers[i]];
						ByteBuffer buffer = socket.write_buffer;
						lock (buffer)
						{
							int length = socket.Socket.Send(buffer.array, buffer.offset, buffer.length, SocketFlags.None);
							buffer.Pop(length);
							socket.need_send = buffer.length != 0;
							if (settings.events.write != null)
							{
								Loop.Run(delegate()
								{
									if (settings.events.write != null)
										settings.events.write(socket, length);
								});
							}
						}
					}
					for (int i = 0; i < reads.Count; i++)
					{
						NetHandlerImpl socket = sockethandlers[reads[i]];
						try
						{
							int total = 0;
							while (socket.Socket.Available > 0)
							{
								int length = socket.Socket.Receive(bytes);
								if (length == 0)
								{
									break;
								}
								total += length;
								socket.OnReceive(bytes, length);
							}
							if (total == 0)
							{
								lock (deletesockets)
								{
									deletesockets.Add(socket);
								}
							}
							else
							{
								if (settings.events.read != null)
								{
									Loop.Run(delegate()
									{
										if (settings.events.read != null)
											settings.events.read(socket, total);
									});
								}
							}
						}
						catch (SocketException e)
						{
							if (e.SocketErrorCode == SocketError.ConnectionReset ||
								e.SocketErrorCode == SocketError.ConnectionAborted ||
								e.SocketErrorCode == SocketError.NotConnected ||
								e.SocketErrorCode == SocketError.Shutdown)
							{
								lock (deletesockets)
								{
									deletesockets.Add(socket);
								}
							}
							else
							{
								if (settings.events.exception != null)
								{
									Loop.Run(delegate()
									{
										if (settings.events.exception != null)
											settings.events.exception(socket, e);
									});
								}
							}
						}
						catch (Exception e)
						{
							if (settings.events.exception != null)
							{
								Loop.Run(delegate()
								{
									if (settings.events.exception != null)
										settings.events.exception(socket, e);
								});
							}
						}
					}
					reads.Clear();
					writers.Clear();
					errors.Clear();
					sockethandlers.Clear();
				}
			}).Start();
		}
		#endregion

		#region 连接
		private void ConnectImpl(string host, int port, int timeout, Action<NetHandler, Exception> callback)
		{
			if (!valid)
				throw new ObjectDisposedException(ToString());
			Init();
			IPAddress ip;
			if (IPAddress.TryParse(host, out ip))
			{
				NetHandlerImpl socket = new NetHandlerImpl(ip.AddressFamily) { Manager = this };
				Timer timer = new Timer(state =>
				{
					socket.Socket.Close();
				}, null, Timeout.Infinite, Timeout.Infinite);
				socket.Socket.BeginConnect(ip, port, result =>
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
						lock (newsockets)
						{
							newsockets.Add(socket);
						}
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
							AddressFamily family = AddressFamily.InterNetworkV6;
							for (int i = 0; i < iplist.Length; ++i)
							{
								if (iplist[i].AddressFamily == AddressFamily.InterNetwork)
								{
									family = AddressFamily.InterNetwork;
									break;
								}
							}
							NetHandlerImpl socket = new NetHandlerImpl(family) { Manager = this };
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
									lock (newsockets)
									{
										newsockets.Add(socket);
									}
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
		}
		#endregion

		#region 网络连接的具体类
		private class NetHandlerImpl : NetHandler
		{
			public NetManager Manager;
			public bool need_send = false;
			public readonly ByteBuffer write_buffer = new ByteBuffer();
			public readonly Socket Socket;
			private Request request;

			public NetHandlerImpl(AddressFamily family)
			{
				Socket = new Socket(family, SocketType.Stream, ProtocolType.Tcp);
				Socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Linger, new LingerOption(false, 0));
			}

			public NetHandlerImpl(Socket socket)
			{
				Socket = socket;
				Socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Linger, new LingerOption(false, 0));
			}

			public override void Close()
			{
				lock (Manager.deletesockets)
				{
					Manager.deletesockets.Add(this);
				}
			}

			public override bool Send(BufferSegment seg)
			{
				lock (write_buffer)
				{
					if (write_buffer.length >= Manager.settings.sendbytes)
						return false;
					need_send = true;
					write_buffer.Write(seg);
				}
				return true;
			}

			public override bool Send(BufferSegment seg1, BufferSegment seg2)
			{
				lock (write_buffer)
				{
					if (write_buffer.length >= Manager.settings.sendbytes)
						return false;
					need_send = true;
					write_buffer.Write(seg1);
					write_buffer.Write(seg2);
				}
				return true;
			}

			public override bool Send(BufferSegment seg1, BufferSegment seg2, BufferSegment seg3)
			{
				lock (write_buffer)
				{
					if (write_buffer.length >= Manager.settings.sendbytes)
						return false;
					need_send = true;
					write_buffer.Write(seg1);
					write_buffer.Write(seg2);
					write_buffer.Write(seg3);
				}
				return true;
			}

			public override bool Send(BufferSegment seg, params BufferSegment[] segs)
			{
				lock (write_buffer)
				{
					if (write_buffer.length >= Manager.settings.sendbytes)
						return false;
					need_send = true;
					write_buffer.Write(seg);
					for (int i = 0; i < segs.Length; ++i)
					{
						write_buffer.Write(segs[i]);
					}
				}
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

			public void OnReceive(byte[] bytes, int length)
			{
				int offset = 0;
				int size = length - offset;
				if (request == null)
					request = Manager.NewRequest();
				try
				{
					while (size > 0 && request.Input(bytes, offset, ref size))
					{
						offset += size;
						size = length - offset;
						Request old = request;
						request = Manager.NewRequest();
						Loop.Run(delegate()
						{
							if (Manager.settings.events.request != null)
							{
								Manager.settings.events.request(this, old);
							}
							old.Execute(this);
							Manager.ReleaseRequest(old);
						});
					}
				}
				catch (Exception e)
				{
					Socket.Close();
					if (Manager.settings.events.exception != null)
					{
						Loop.Run(delegate()
						{
							if (Manager.settings.events.exception != null)
								Manager.settings.events.exception(this, e);
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
			private static List<Action> actions = new List<Action>();
			private static List<Action> actions_tmp = new List<Action>();
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
					lock (actions)
					{
						actions.Add(action);
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
					lock (actions)
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

				void OnApplicationQuit()
				{
					IsRunning = false;
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