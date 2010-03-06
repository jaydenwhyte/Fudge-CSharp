﻿/* <!--
 * Copyright (C) 2009 - 2010 by OpenGamma Inc. and other contributors.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 * 
 *     http://www.apache.org/licenses/LICENSE-2.0
 *     
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 * -->
 */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Fudge.Types;
using Fudge.Encodings;
using System.Collections;
using System.Diagnostics;

namespace Fudge.Serialization
{
    /// <summary>
    /// Provides an implementation of <see cref="IFudgeSerializer"/> used by the <see cref="FudgeSerializer"/>.
    /// </summary>
    /// <remarks>
    /// You should not need to use this class directly.
    /// </remarks>
    internal class FudgeSerializationContext : IFudgeSerializer
    {
        private readonly FudgeContext context;
        private readonly IFudgeStreamWriter writer;
        private readonly Queue<object> encodeQueue = new Queue<object>();
        private readonly Dictionary<object, int> idMap;     // Tracks IDs of objects that have already been serialised (or are in the process)
        private readonly Dictionary<Type, int> lastTypes = new Dictionary<Type, int>();     // Tracks the last object of a given type
        private readonly SerializationTypeMap typeMap;
        private readonly IFudgeTypeMappingStrategy typeMappingStrategy;
        private readonly IndexedStack<State> inlineStack = new IndexedStack<State>();       // Used to check for cycles in inlined messages and keep track of the index of the current message
        private int currentMessageId = 0;

        public FudgeSerializationContext(FudgeContext context, SerializationTypeMap typeMap, IFudgeStreamWriter writer, IFudgeTypeMappingStrategy typeMappingStrategy)
        {
            this.context = context;
            this.writer = writer;
            this.idMap = new Dictionary<object, int>();     // TODO 2009-10-18 t0rx -- Worry about HashCode and Equals implementations
            this.typeMap = typeMap;
            this.typeMappingStrategy = typeMappingStrategy;
        }

        public void QueueObject(object obj)
        {
            encodeQueue.Enqueue(obj);
        }

        public object PopQueuedObject()
        {
            if (encodeQueue.Count == 0)
            {
                return null;
            }

            return encodeQueue.Dequeue();
        }

        private int RegisterObject(object obj)
        {
            idMap[obj] = currentMessageId;      // There is the possibility that an object is serialised in-line twice - we take the later so that relative references are smaller
            return currentMessageId;
        }

        public void SerializeContents(object obj, int index, IMutableFudgeFieldContainer msg)
        {
            CheckForInlineCycles(obj);

            inlineStack.Push(new State(obj, index));

            var surrogateFactory = typeMap.GetSurrogateFactory(obj.GetType());
            if (surrogateFactory == null)
            {
                // Unknown type
                throw new ArgumentOutOfRangeException("Type \"" + obj.GetType().FullName + "\" not registered, cannot serialize");
            }
            var surrogate = surrogateFactory(context);

            surrogate.Serialize(obj, msg, this);

            inlineStack.Pop();
        }

        private void CheckForInlineCycles(object obj)
        {
            for (int i = inlineStack.Count - 1; i >= 0; i--)
            {
                if (inlineStack[i].Obj == obj)
                {
                    throw new FudgeRuntimeException("Cycle detected in inlined objects at object of type " + obj.GetType());
                }
            }
        }

        public void SerializeGraph(object graph)
        {
            Debug.Assert(currentMessageId == 0);

            RegisterObject(graph);

            var msg = new StreamingMessage(this);

            writer.StartMessage();
            WriteTypeInformation(graph, currentMessageId, msg);
            UpdateLastTypeInfo(graph);
            SerializeContents(graph, currentMessageId, msg);
            writer.EndMessage();
        }

        private void UpdateLastTypeInfo(object obj)
        {
            lastTypes[obj.GetType()] = currentMessageId;
        }

        private void WriteTypeInformation(object obj, int id, IMutableFudgeFieldContainer msg)
        {
            Type type = obj.GetType();
            int lastSeen;
            if (lastTypes.TryGetValue(type, out lastSeen))
            {
                // Already had something of this type
                int offset = lastSeen - id;
                msg.Add(null, FudgeSerializer.TypeIdFieldOrdinal, PrimitiveFieldTypes.IntType, offset);
            }
            else
            {
                // Not seen before, so write out with base types
                for (Type currentType = type; currentType != typeof(object); currentType = currentType.BaseType)
                {
                    string typeName = typeMappingStrategy.GetName(currentType);
                    msg.Add(null, FudgeSerializer.TypeIdFieldOrdinal, StringFieldType.Instance, typeName);
                }
            }
        }

        #region IFudgeSerializer Members

        /// <inheritdoc/>
        public FudgeContext Context
        {
            get { return context; }
        }

        /// <inheritdoc/>
        public void WriteInline(IMutableFudgeFieldContainer msg, string fieldName, int? ordinal, object obj)
        {
            if (obj != null)
            {
                WriteObject(fieldName, ordinal, obj, false, false);
            }
        }

        #endregion

        private void Write(string fieldName, int? ordinal, FudgeFieldType type, object value)
        {
            // TODO 20100306 t0rx -- Have to track if the value is a message to inc the count for references
            if (type == null)
            {
                type = context.TypeHandler.DetermineTypeFromValue(value);
            }
            if (type == null)
            {
                WriteObject(fieldName, ordinal, value, true, true);
            }
            else
            {
                writer.WriteField(fieldName, ordinal, type, value);
            }
        }

        private void WriteObject(string fieldName, int? ordinal, object value, bool allowRefs, bool writeTypeInfo)
        {
            if (allowRefs)
            {
                int previousId = GetRefId(value);
                if (previousId != -1)
                {
                    // Refs are relative to the containing message
                    int diff = previousId - inlineStack.Peek().Index;
                    writer.WriteField(fieldName, ordinal, PrimitiveFieldTypes.IntType, diff);
                    return;
                }
            }

            // New object
            currentMessageId++;
            RegisterObject(value);

            var subMsg = new StreamingMessage(this);
            writer.StartSubMessage(fieldName, ordinal);
            if (writeTypeInfo)
            {
                WriteTypeInformation(value, currentMessageId, subMsg);
            }
            UpdateLastTypeInfo(value);
            SerializeContents(value, currentMessageId, subMsg);
            writer.EndSubMessage();
        }

        private int GetRefId(object obj)
        {
            int id;
            if (!idMap.TryGetValue(obj, out id))
            {
                return -1;
            }

            return id;
        }

        /// <summary>
        /// StreamingMessage appears to the user like it is a normal message, but rather than adding
        /// fields it's actually streaming them out to the writer.
        /// </summary>
        private sealed class StreamingMessage : IMutableFudgeFieldContainer
        {
            private readonly FudgeSerializationContext serializationContext;

            public StreamingMessage(FudgeSerializationContext serializationContext)
            {
                this.serializationContext = serializationContext;
            }

            #region IMutableFudgeFieldContainer Members

            public void Add(IFudgeField field)
            {
                serializationContext.Write(field.Name, field.Ordinal, field.Type, field.Value);
            }

            public void Add(string name, object value)
            {
                serializationContext.Write(name, null, null, value);
            }

            public void Add(int? ordinal, object value)
            {
                serializationContext.Write(null, ordinal, null, value);
            }

            public void Add(string name, int? ordinal, object value)
            {
                serializationContext.Write(name, ordinal, null, value);
            }

            public void Add(string name, int? ordinal, FudgeFieldType type, object value)
            {
                serializationContext.Write(name, ordinal, type, value);
            }

            #endregion

            #region IFudgeFieldContainer Members

            public short GetNumFields()
            {
                throw new NotImplementedException();
            }

            public IList<IFudgeField> GetAllFields()
            {
                throw new NotImplementedException();
            }

            public IList<string> GetAllFieldNames()
            {
                throw new NotImplementedException();
            }

            public IFudgeField GetByIndex(int index)
            {
                throw new NotImplementedException();
            }

            public IList<IFudgeField> GetAllByOrdinal(int ordinal)
            {
                throw new NotImplementedException();
            }

            public IFudgeField GetByOrdinal(int ordinal)
            {
                throw new NotImplementedException();
            }

            public IList<IFudgeField> GetAllByName(string name)
            {
                throw new NotImplementedException();
            }

            public IFudgeField GetByName(string name)
            {
                throw new NotImplementedException();
            }

            public object GetValue(string name)
            {
                throw new NotImplementedException();
            }

            public T GetValue<T>(string name)
            {
                throw new NotImplementedException();
            }

            public object GetValue(string name, Type type)
            {
                throw new NotImplementedException();
            }

            public object GetValue(int ordinal)
            {
                throw new NotImplementedException();
            }

            public T GetValue<T>(int ordinal)
            {
                throw new NotImplementedException();
            }

            public object GetValue(int ordinal, Type type)
            {
                throw new NotImplementedException();
            }

            public object GetValue(string name, int? ordinal)
            {
                throw new NotImplementedException();
            }

            public T GetValue<T>(string name, int? ordinal)
            {
                throw new NotImplementedException();
            }

            public object GetValue(string name, int? ordinal, Type type)
            {
                throw new NotImplementedException();
            }

            public double? GetDouble(string fieldName)
            {
                throw new NotImplementedException();
            }

            public double? GetDouble(int ordinal)
            {
                throw new NotImplementedException();
            }

            public float? GetFloat(string fieldName)
            {
                throw new NotImplementedException();
            }

            public float? GetFloat(int ordinal)
            {
                throw new NotImplementedException();
            }

            public long? GetLong(string fieldName)
            {
                throw new NotImplementedException();
            }

            public long? GetLong(int ordinal)
            {
                throw new NotImplementedException();
            }

            public int? GetInt(string fieldName)
            {
                throw new NotImplementedException();
            }

            public int? GetInt(int ordinal)
            {
                throw new NotImplementedException();
            }

            public short? GetShort(string fieldName)
            {
                throw new NotImplementedException();
            }

            public short? GetShort(int ordinal)
            {
                throw new NotImplementedException();
            }

            public sbyte? GetSByte(string fieldName)
            {
                throw new NotImplementedException();
            }

            public sbyte? GetSByte(int ordinal)
            {
                throw new NotImplementedException();
            }

            public bool? GetBoolean(string fieldName)
            {
                throw new NotImplementedException();
            }

            public bool? GetBoolean(int ordinal)
            {
                throw new NotImplementedException();
            }

            public string GetString(string fieldName)
            {
                throw new NotImplementedException();
            }

            public string GetString(int ordinal)
            {
                throw new NotImplementedException();
            }

            public IFudgeFieldContainer GetMessage(string fieldName)
            {
                throw new NotImplementedException();
            }

            public IFudgeFieldContainer GetMessage(int ordinal)
            {
                throw new NotImplementedException();
            }

            #endregion

            #region IEnumerable<IFudgeField> Members

            public IEnumerator<IFudgeField> GetEnumerator()
            {
                throw new NotImplementedException();
            }

            #endregion

            #region IEnumerable Members

            IEnumerator IEnumerable.GetEnumerator()
            {
                throw new NotImplementedException();
            }

            #endregion

        }

        /// <summary>
        /// You can't index into Stack{T} so this gives us something that we can index but looks like a stack
        /// </summary>
        /// <typeparam name="T"></typeparam>
        private sealed class IndexedStack<T> : List<T>
        {
            public void Push(T val)
            {
                base.Add(val);
            }

            public T Pop()
            {
                int index = this.Count - 1;
                if (index == -1)
                    throw new InvalidOperationException();

                var result = this[index];
                this.RemoveAt(index);
                return result;
            }

            public T Peek()
            {
                int index = this.Count - 1;
                if (index == -1)
                    throw new InvalidOperationException();

                return this[index];
            }
        }

        private struct State
        {
            private readonly object obj;
            private readonly int index;

            public State(object obj, int index)
            {
                this.obj = obj;
                this.index = index;
            }

            public object Obj { get { return obj; } }
            public int Index { get { return index; } }
        }
    }
}
