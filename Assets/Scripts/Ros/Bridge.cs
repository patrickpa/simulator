/**
 * Copyright (c) 2018 LG Electronics, Inc.
 *
 * This software contains code licensed as described in LICENSE.
 *
 */


﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using WebSocketSharp;
using SimpleJSON;
using System.Reflection;
using System.Collections;
using static Apollo.Utils;

namespace Ros
{
    public enum Status
    {
        Disconnected,
        Connecting,
        Connected,
        Disconnecting,
    }

    public enum SerialType
    {
        JSON,
        HDMap,
    }

    public class Bridge
    {
        public static bool canConnect = false; // THIS IS VERY VERY BAD!! PLEASE DONT USE GLOBAL VARIABLES :(

        //Msg Logging Variables//
        public bool enable_msg_log = false;
        private StringBuilder csv = new StringBuilder();
        private Stopwatch StartTimer = new Stopwatch();
        //END//

        WebSocket Socket;

        public string Address { get; private set; }
        public int Port { get; private set; }

        struct Subscription
        {
            public string Topic;
            public Delegate Callback;
        }

        public struct Topic
        {
            public string Name;
            public string Type;
        }

        public List<Topic> TopicSubscriptions = new List<Topic>();
        public List<Topic> TopicPublishers = new List<Topic>();

        List<IRosClient> Publishers = new List<IRosClient>();
        List<Subscription> Subscriptions = new List<Subscription>();
        Dictionary<string, Delegate> Services = new Dictionary<string, Delegate>();
        Queue<Action> QueuedActions = new Queue<Action>();
        Queue<string> QueuedSends = new Queue<string>();

        public int Version { get; private set; }
        public Status Status { get; private set; }

        static Bridge()
        {
            // incrase send buffer size for WebSocket C# library
            // FragmentLength is internal filed, that's why reflection is used here
            var f = typeof(WebSocket).GetField("FragmentLength", BindingFlags.Static | BindingFlags.NonPublic);
            f.SetValue(null, 65536 - 8);
        }

        public Bridge()
        {
            Status = Status.Disconnected;
        }

        public void Connect(string address, int port, int version)
        {

            Address = address;
            Port = port;
            Version = version;
            try
            {
                Socket = new WebSocket(String.Format("ws://{0}:{1}", address, port));
                Socket.WaitTime = TimeSpan.FromSeconds(1.0);
                Socket.OnMessage += OnMessage;
                Socket.OnOpen += OnOpen;
                Socket.OnError += OnError;
                Socket.OnClose += OnClose;
                Status = Status.Connecting;
                Socket.ConnectAsync();
                MsgLogInit();
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError(e);
            }
        }

        public void Close()
        {
            if (Socket != null)
            {
                if (Socket.ReadyState == WebSocketState.Open)
                {
                    Status = Status.Disconnecting;
                    Socket.CloseAsync();
                    MsgLogWriteOut();
                }
            }
        }

        void OnClose(object sender, CloseEventArgs args)
        {
            //if (!args.WasClean)
            //{
            //    UnityEngine.Debug.LogError(args.Reason);
            //}
            Subscriptions.Clear();
            Services.Clear();
            QueuedActions.Clear();
            QueuedSends.Clear();
            TopicSubscriptions.Clear();
            TopicPublishers.Clear();
            Status = Status.Disconnected;
        }

        void OnError(object sender, ErrorEventArgs args)
        {
            UnityEngine.Debug.LogError(args.Message);

            if (args.Exception != null)
            {
                UnityEngine.Debug.LogError(args.Exception.ToString());
            }
        }

        void OnOpen(object sender, EventArgs args)
        {
            Status = Status.Connected;
            foreach (var publisher in Publishers)
            {
                publisher.OnRosConnected();
            }
        }

        void OnMessage(object sender, MessageEventArgs args)
        {
            var json = JSONNode.Parse(args.Data);
            string op = json["op"];

            // receive a msg from a publisher - Receive
            if (op == "publish")
            {
                string topic = json["topic"];

                object data = null;

                foreach (var sub in Subscriptions)
                {
                    if (sub.Topic == topic)
                    {
                        if (data == null)
                        {
                            var msg = json["msg"];
                            data = Unserialize(Version, msg, sub.Callback.Method.GetParameters()[0].ParameterType);
                        }
                        lock (QueuedActions)
                        {
                            QueuedActions.Enqueue(() => 
                            {
                                // code to write to a file
                                MsgLogWrite("receive", topic);

                                sub.Callback.DynamicInvoke(data);
                            });
                        }
                    }
                }
            }

            // receive a msg calling for a service - Send
            else if (op == "call_service")
            {

                var service = json["service"];
                var id = json["id"];
                if (!Services.ContainsKey(service))
                {
                    return;
                }

                // code to write to a file
                MsgLogWrite("call_service_receive", service);

                var callback = Services[service];

                var argType = callback.Method.GetParameters()[0].ParameterType;
                var retType = callback.Method.ReturnType;

                var arg = Unserialize(Version, json["args"], argType);

                lock (QueuedActions)
                {
                    QueuedActions.Enqueue(() =>
                    {
                        var ret = callback.DynamicInvoke(arg);

                        var sb = new StringBuilder(128);
                        sb.Append('{');
                        {
                            sb.Append("\"op\":\"service_response\",");

                            sb.Append("\"id\":");
                            sb.Append(id.ToString());
                            sb.Append(",");

                            sb.Append("\"service\":\"");
                            sb.Append(service);
                            sb.Append("\",");

                            sb.Append("\"values\":");
                            Serialize(Version, sb, retType, ret);
                            sb.Append(",");

                            sb.Append("\"result\":true");
                        }
                        sb.Append('}');

                        // code to write to a file
                        MsgLogWrite("service_response_sent", service);

                        var s = sb.ToString();
                        Socket.SendAsync(s, ok => { });
                    });
                }
            }
            else if (op == "set_level")
            {
                // ignore these
            }
            else
            {
                UnityEngine.Debug.Log(String.Format("Unknown RosBridge op {0}", op));
            }
        }

        public void AddPublisher(IRosClient publisher)
        {
            if (Status == Status.Connected)
            {
                publisher.OnRosConnected();
            }
            Publishers.Add(publisher);
        }

        public void AddPublisher<T>(string topic)
        {
            if (Socket.ReadyState != WebSocketState.Open)
            {
                throw new InvalidOperationException("socket not open");
            }

            var type = GetMessageType<T>();

            var sb = new StringBuilder(256);
            sb.Append('{');
            {
                sb.Append("\"op\":\"advertise\",");

                sb.Append("\"topic\":\"");
                sb.Append(topic);
                sb.Append("\",");

                sb.Append("\"type\":\"");
                sb.Append(type);
                sb.Append("\"");
            }
            sb.Append('}');

            TopicPublishers.Add(new Topic()
            {
                Name = topic,
                Type = type,
            });

            // code to write to a file
            MsgLogWrite("publisher", topic);

            //UnityEngine.Debug.Log("Adding publisher " + sb.ToString());
            Socket.SendAsync(sb.ToString(), ok => { });
        }

        public void AddService<Args, Result>(string service, Func<Args, Result> callback)
        {
            if (Socket.ReadyState != WebSocketState.Open)
            {
                throw new InvalidOperationException("socket not open");
            }

            var type = GetMessageType<Args>();
            GetMessageType<Result>();

            var sb = new StringBuilder(256);
            sb.Append('{');
            {
                sb.Append("\"op\":\"advertise_service\",");

                sb.Append("\"type\":\"");
                sb.Append(type);
                sb.Append("\",");

                sb.Append("\"service\":\"");
                sb.Append(service);
                sb.Append("\"");
            }
            sb.Append('}');

            // code to write to a file
            MsgLogWrite("add_service", service);

            Services.Add(service, callback);
            Socket.SendAsync(sb.ToString(), ok => { });
        }

        // send msg to a topic
        public void Publish<T>(string topic, T message, Action completed = null)
        {
            var sb = new StringBuilder(128);
            sb.Append('{');
            {
                sb.Append("\"op\":\"publish\",");

                sb.Append("\"topic\":\"");
                sb.Append(topic);
                sb.Append("\",");

                sb.Append("\"msg\":");
                Serialize(Version, sb, typeof(T), message);
            }
            sb.Append('}');

            var s = sb.ToString();
            //UnityEngine.Debug.Log("Publishing " + s.Substring(0, s.Length > 200 ? 200 : s.Length));

            if (completed == null)
            {
                // code to write to a file
                MsgLogWrite("publish", topic);

                Socket.SendAsync(s, ok => { });
            }
            else
            {
                // code to write to a file
                MsgLogWrite("publish", topic);

                Socket.SendAsync(s, ok => completed());
            }
        }

        public void Subscribe<T>(string topic, Action<T> callback)
        {
            if (Socket.ReadyState != WebSocketState.Open)
            {
                throw new InvalidOperationException("socket not open");
            }

            var type = GetMessageType<T>();

            var sb = new StringBuilder(256);
            sb.Append('{');
            {
                sb.Append("\"op\":\"subscribe\",");

                sb.Append("\"topic\":\"");
                sb.Append(topic);
                sb.Append("\",");

                sb.Append("\"type\":\"");
                sb.Append(type);
                sb.Append("\"");
            }
            sb.Append('}');

            Subscriptions.Add(new Subscription()
            {
                Topic = topic,
                Callback = callback,
            });

            TopicSubscriptions.Add(new Topic()
            {
                Name = topic,
                Type = type,
            });

            // code to write to a file
            MsgLogWrite("subscribe", topic);

            //UnityEngine.Debug.Log("Adding subscriber " + sb.ToString());
            Socket.SendAsync(sb.ToString(), ok => { });
        }

        public void Update()
        {
            lock (QueuedActions)
            {
                while (QueuedActions.Count > 0)
                {
                    QueuedActions.Dequeue()();
                }
            }
        }

        static bool CheckBasicType(Type type)
        {
            if (type.IsNullable())
            {
                type = Nullable.GetUnderlyingType(type);
            }

            if (BuiltinMessageTypes.ContainsKey(type))
            {
                return true;
            }
            if (type == typeof(string))
            {
                return true;
            }
            if (type.IsEnum)
            {
                return true;
            }
            return false;
        }

        static readonly Dictionary<Type, string> BuiltinMessageTypes = new Dictionary<Type, string> {
            { typeof(bool), "std_msgs/Bool" },
            { typeof(sbyte), "std_msgs/Int8" },
            { typeof(short), "std_msgs/Int16" },
            { typeof(int), "std_msgs/Int32" },
            { typeof(long), "std_msgs/Int64" },
            { typeof(byte), "std_msgs/UInt8" },
            { typeof(ushort), "std_msgs/UInt16" },
            { typeof(uint), "std_msgs/UInt32" },
            { typeof(ulong), "std_msgs/UInt64" },
            { typeof(float), "std_msgs/Float32" },
            { typeof(double), "std_msgs/Float64" },
            { typeof(string), "std_msgs/String" },
        };

        string GetMessageType<T>()
        {
            string type;
            if (BuiltinMessageTypes.TryGetValue(typeof(T), out type))
            {
                return type;
            }

            object[] attributes = typeof(T).GetCustomAttributes(typeof(MessageTypeAttribute), false);
            if (attributes == null || attributes.Length == 0)
            {
                throw new Exception(String.Format("Type {0} does not have {1} attribute", typeof(T).Name, typeof(MessageTypeAttribute).Name));
            }

            MessageTypeAttribute attribute = (MessageTypeAttribute)attributes[0];
            return Version == 1 ? attribute.Type : attribute.Type2;
        }

        static void Escape(StringBuilder sb, string text)
        {
            foreach (char c in text)
            {
                switch (c)
                {
                    case '\\': sb.Append('\\'); sb.Append('\\'); break;
                    case '\"': sb.Append('\\'); sb.Append('"'); break;
                    case '\n': sb.Append('\\'); sb.Append('n'); break;
                    case '\r': sb.Append('\\'); sb.Append('r'); break;
                    case '\t': sb.Append('\\'); sb.Append('t'); break;
                    case '\b': sb.Append('\\'); sb.Append('b'); break;
                    case '\f': sb.Append('\\'); sb.Append('f'); break;
                    default: sb.Append(c); break;
                }
            }
        }

        public static void SerializeInternal(int version, StringBuilder sb, Type type, object message, SerialType sType = SerialType.JSON, string keyName = "")
        {
            var nulChr = (object)null;

            if (type.IsNullable())
            {
                type = Nullable.GetUnderlyingType(type);
            }
            if (message == null)
            {
                message = type.TypeDefaultValue(); //only underlying value type will be given a default value
            }

            //
            if (type == typeof(string))
            {
                sb.Append('"');
                if (!string.IsNullOrEmpty((string)message))
                {
                    Escape(sb, message.ToString());
                }
                sb.Append('"');
            }
            else if (type.IsEnum)
            {
                if (sType == SerialType.JSON)
                {
                    var etype = type.GetEnumUnderlyingType();
                    SerializeInternal(version, sb, etype, Convert.ChangeType(message, etype), sType: sType);
                }
                else if (sType == SerialType.HDMap)
                {
                    sb.Append(message.ToString());
                }
            }
            else if (BuiltinMessageTypes.ContainsKey(type))
            {
                if (type == typeof(bool))
                {
                    sb.Append(message.ToString().ToLower());
                }
                else
                {
                    sb.Append(message.ToString());
                }
            }
            else if (type == typeof(PartialByteArray) && sType == SerialType.JSON)
            {
                PartialByteArray arr = (PartialByteArray)message;
                if (version == 1)
                {
                    sb.Append('"');
                    if (arr.Base64 == null)
                    {
                        sb.Append(System.Convert.ToBase64String(arr.Array, 0, arr.Length));
                    }
                    else
                    {
                        sb.Append(arr.Base64);
                    }
                    sb.Append('"');
                }
                else
                {
                    sb.Append(sType == SerialType.JSON ? '[' : nulChr);
                    for (int i = 0; i < arr.Length; i++)
                    {
                        sb.Append(arr.Array[i]);
                        if (i < arr.Length - 1)
                        {
                            sb.Append(sType == SerialType.JSON ? ',' : ' ');
                        }
                    }
                    sb.Append(sType == SerialType.JSON ? ']' : nulChr);
                }
            }
            else if (type.IsArray)
            {
                if (type.GetElementType() == typeof(byte) && version == 1)
                {
                    sb.Append('"');
                    sb.Append(System.Convert.ToBase64String((byte[])message));
                    sb.Append('"');
                }
                else
                {
                    Array arr = (Array)message;
                    sb.Append(sType == SerialType.JSON ? '[' : nulChr);
                    for (int i = 0; i < arr.Length; i++)
                    {
                        if (sType == SerialType.HDMap && i > 0)
                        {
                            sb.Append(keyName);
                        }
                        SerializeInternal(version, sb, type.GetElementType(), arr.GetValue(i), sType: sType);
                        if (i < arr.Length - 1)
                        {
                            sb.Append(sType == SerialType.JSON ? ',' : ' ');
                        }
                    }
                    sb.Append(sType == SerialType.JSON ? ']' : nulChr);
                }
            }
            else if (type.IsGenericList())
            {
                IList list = (IList)message;
                sb.Append(sType == SerialType.JSON ? '[' : nulChr);
                for (int i = 0; i < list.Count; i++)
                {
                    if (sType == SerialType.HDMap && i > 0)
                    {
                        sb.Append(keyName);
                    }
                    SerializeInternal(version, sb, list[i].GetType(), list[i], sType: sType);
                    if (i < list.Count - 1)
                    {
                        sb.Append(sType == SerialType.JSON ? ',' : ' ');
                    }
                }
                sb.Append(sType == SerialType.JSON ? ']' : nulChr);
            }
            else if (type == typeof(Time))
            {
                Time t = (Time)message;
                if (version == 1)
                {
                    sb.AppendFormat("{{\"secs\":{0},\"nsecs\":{1}}}", (uint)t.secs, (uint)t.nsecs);
                }
                else
                {
                    sb.AppendFormat("{{\"sec\":{0},\"nanosec\":{1}}}", (int)t.secs, (uint)t.nsecs);
                }
            }
            else
            {
                var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);

                sb.Append('{');  
                
                for (int i = 0; i < fields.Length; i++)
                {
                    var field = fields[i];
                    if (version == 2 && type == typeof(Header) && field.Name == "seq")
                    {
                        continue;
                    }

                    var fieldType = field.FieldType;
                    var fieldValue = field.GetValue(message);

                    if (fieldValue != null && typeof(IOneOf).IsAssignableFrom(fieldType)) //only when it is a OneOf field
                    {
                        var oneof = fieldValue as IOneOf;
                        if (oneof != null) //only when this is a non-null OneOf
                        {
                            var oneInfo = oneof.GetOne();
                            if (oneInfo.Value != null) //only hwne at least one subfield assgined
                            {
                                var oneFieldName = oneInfo.Key;
                                var oneFieldValue = oneInfo.Value;
                                var oneFieldType = oneInfo.Value.GetType();

                                sb.Append(sType == SerialType.JSON ? '"' : nulChr);
                                sb.Append(oneFieldName);
                                sb.Append(sType == SerialType.JSON ? '"' : nulChr);

                                if (sType == SerialType.HDMap)
                                {
                                    if (CheckBasicType(oneFieldType) || (oneFieldType.IsCollectionType() && CheckBasicType(oneFieldType.GetCollectionElement())))
                                    {
                                        sb.Append(':');
                                    }
                                    SerializeInternal(version, sb, oneFieldType, oneFieldValue, sType: sType, keyName: oneFieldName);
                                }
                                else if (sType == SerialType.JSON)
                                {
                                    sb.Append(':');
                                    SerializeInternal(version, sb, oneFieldType, oneFieldValue, sType: sType);
                                }
                                sb.Append(sType == SerialType.JSON ? ',' : ' ');
                            }
                        }
                    }
                    else if (fieldValue != null || (fieldType.IsNullable() && Attribute.IsDefined(field, typeof(global::Apollo.RequiredAttribute))))
                    {
                        sb.Append(sType == SerialType.JSON ? '"' : nulChr);
                        sb.Append(field.Name);
                        sb.Append(sType == SerialType.JSON ? '"' : nulChr);

                        if (sType == SerialType.HDMap)
                        {
                            if (CheckBasicType(fieldType) || (fieldType.IsCollectionType() && CheckBasicType(fieldType.GetCollectionElement())))
                            {
                                sb.Append(':');
                            }
                            SerializeInternal(version, sb, fieldType, fieldValue, sType: sType, keyName: field.Name);
                        }
                        else if (sType == SerialType.JSON)
                        {
                            sb.Append(':');
                            SerializeInternal(version, sb, fieldType, fieldValue, sType: sType);
                        }
                        sb.Append(sType == SerialType.JSON ? ',' : ' ');
                    }
                }
                if (sType == SerialType.JSON)
                {
                    if (sb[sb.Length - 1] == ',')
                    {
                        sb.Remove(sb.Length - 1, 1);
                    }
                }

                sb.Append('}');
            }
        }

        static void Serialize(int version, StringBuilder sb, Type type, object message)
        {
            if (type == typeof(string))
            {
                sb.Append("{");
                sb.Append("\"data\":");
                sb.Append('"');
                Escape(sb, message.ToString());
                sb.Append('"');
                sb.Append('}');
            }
            else if (BuiltinMessageTypes.ContainsKey(type))
            {
                sb.Append("{");
                sb.Append("\"data\":");
                sb.Append(message.ToString());
                sb.Append('}');
            }
            else if (type == typeof(Ros.Time))
            {
                sb.Append("{");
                sb.Append("\"data\":");
                SerializeInternal(version, sb, type, message);
                sb.Append('}');
            }
            else
            {
                SerializeInternal(version, sb, type, message, sType: SerialType.JSON);
            }
        }

        static object UnserializeInternal(int version, JSONNode node, Type type)
        {
            if (type == typeof(bool))
            {
                return node.AsBool;
            }
            else if (type == typeof(sbyte))
            {
                return short.Parse(node.Value);
            }
            else if (type == typeof(int))
            {
                return int.Parse(node.Value);
            }
            else if (type == typeof(long))
            {
                return long.Parse(node.Value);
            }
            else if (type == typeof(byte))
            {
                return byte.Parse(node.Value);
            }
            else if (type == typeof(ushort))
            {
                return ushort.Parse(node.Value);
            }
            else if (type == typeof(uint))
            {
                return uint.Parse(node.Value);
            }
            else if (type == typeof(ulong))
            {
                return ulong.Parse(node.Value);
            }
            else if (type == typeof(float))
            {
                return node.AsFloat;
            }
            else if (type == typeof(double))
            {
                return node.AsDouble;
            }
            else if (type == typeof(string))
            {
                return node.Value;
            }
            else if (type == typeof(PartialByteArray))
            {
                var nodeArr = node.AsArray;

                if (type.GetElementType() == typeof(byte) && nodeArr == null)
                {
                    var array = System.Convert.FromBase64String(node.Value);
                    return new PartialByteArray()
                    {
                        Array = array,
                        Length = array.Length,
                    };
                }
                else
                {
                    var array = new PartialByteArray()
                    {
                        Array = new byte[node.Count],
                        Length = node.Count,
                    };
                    for (int i = 0; i < node.Count; i++)
                    {
                        array.Array[i] = byte.Parse(nodeArr[i].Value);
                    }
                    return array;
                }
            }
            else if (type.IsArray)
            {
                var nodeArr = node.AsArray;

                if (type.GetElementType() == typeof(byte) && nodeArr == null)
                {
                    return System.Convert.FromBase64String(node.Value);
                }

                var arr = Array.CreateInstance(type.GetElementType(), node.Count);
                for (int i = 0; i < node.Count; i++)
                {
                    arr.SetValue(UnserializeInternal(version, nodeArr[i], type.GetElementType()), i);
                }
                return arr;
            }
            else if (type == typeof(Time))
            {
                var nodeObj = node.AsObject;
                var obj = new Time();
                if (version == 1)
                {
                    obj.secs = uint.Parse(nodeObj["secs"].Value);
                    obj.nsecs = uint.Parse(nodeObj["nsecs"].Value);
                }
                else
                {
                    obj.secs = int.Parse(nodeObj["sec"].Value);
                    obj.nsecs = uint.Parse(nodeObj["nanosec"].Value);
                }
                return obj;
            }
            else if (type.IsEnum)
            {
                var value = node.AsInt;
                var obj = Enum.ToObject(type, value);
                return obj;
            }
            else
            {
                var nodeObj = node.AsObject;
                var obj = Activator.CreateInstance(type);
                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    // UnityEngine.Debug.Log(nodeObj.ToString());
                    if (nodeObj.Contains(field.Name))
                    {
                        var fieldType = field.FieldType;
                        if (fieldType.IsNullable())
                        {
                            fieldType = Nullable.GetUnderlyingType(fieldType);
                        }
                        var value = UnserializeInternal(version, nodeObj[field.Name], fieldType);
                        field.SetValue(obj, value);

                    }
                }
                return obj;
            }
        }

        static object Unserialize(int version, JSONNode node, Type type)
        {
            if (BuiltinMessageTypes.ContainsKey(type) || type == typeof(Ros.Time))
            {
                return UnserializeInternal(version, node["data"], type);
            }
            return UnserializeInternal(version, node, type);
        }

        void MsgLogWrite(string kind, string topic)
        {
            if (enable_msg_log) {
                StartTimer.Stop();

                long lElapsedTicks = StartTimer.ElapsedTicks;
                long lTicksPerSecond = Stopwatch.Frequency;
                double lMilliseconds = 1000.0 * (double)lElapsedTicks / (double)lTicksPerSecond;

                double time_stamp = lMilliseconds / 1000;
                var newMsg = string.Format("{0},{1},{2},{3}", kind, topic, time_stamp, Port);

                csv.AppendLine(newMsg);

                StartTimer.Start();
            }
        }

        void MsgLogInit()
        {
            if (csv.Length == 0 && enable_msg_log) {
                System.IO.File.WriteAllText(string.Format("msgs_log-{0}_{1}.csv", Address, Port), string.Empty);
                StartTimer.Start();
                csv.AppendLine(string.Format("type,topic-service,time_stamp,{0}_{1}", Address, Port));
            }
        }

        void MsgLogWriteOut()
        {
            if (enable_msg_log) {
                System.IO.File.AppendAllText(string.Format("msgs_log-{0}_{1}.csv", Address, Port), csv.ToString());
                csv = new StringBuilder();
            }
        }

    }
}
