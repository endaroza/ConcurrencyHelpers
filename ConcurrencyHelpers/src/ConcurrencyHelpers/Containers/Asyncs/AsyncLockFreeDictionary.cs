// ===========================================================
// Copyright (c) 2014-2015, Enrico Da Ros/kendar.org
// All rights reserved.
// 
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are met:
// 
// * Redistributions of source code must retain the above copyright notice, this
//   list of conditions and the following disclaimer.
// 
// * Redistributions in binary form must reproduce the above copyright notice,
//   this list of conditions and the following disclaimer in the documentation
//   and/or other materials provided with the distribution.
// 
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
// AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
// IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
// DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
// FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
// DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
// SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
// CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
// OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
// ===========================================================

//TODO: Interesting reading http://www.communicraft.com/en/blog/BlogArticle/lock_free_dictionary

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using ConcurrencyHelpers.Interfaces;
using ConcurrencyHelpers.Utils;

namespace ConcurrencyHelpers.Containers.Asyncs
{
	public class AsyncLockFreeDictionary<TKey, TValue> : IDictionary<TKey, TValue>
	{
		// ReSharper disable once PrivateFieldCanBeConvertedToLocalVariable
		private readonly ITimer _timer;
		private readonly LockFreeItem<Dictionary<TKey, TValue>> _dictionary;
		private readonly Dictionary<TKey, TValue> _trustedSource;
		private readonly LockFreeQueue<AsyncCollectionRequest<KeyValuePair<TKey, TValue>>> _requestsQueue;
		public ConcurrentInt64 MaxMessagesPerCycle { get; private set; }
		private ConcurrentInt64 _opverlappingChecker;

		public ITimer Timer
		{
			get
			{
				return _timer;
			}
		}

		public AsyncLockFreeDictionary(ITimer timer = null) :
			this(new Dictionary<TKey, TValue>(), timer)
		{

		}

		public AsyncLockFreeDictionary(IDictionary<TKey, TValue> dictionary, ITimer timer = null)
		{
			MaxMessagesPerCycle = new ConcurrentInt64(100);
			_opverlappingChecker = new ConcurrentInt64();
			_timer = timer ?? new SystemTimer(10, 10);
			_dictionary = new LockFreeItem<Dictionary<TKey, TValue>>(new Dictionary<TKey, TValue>(dictionary))
										{
											Data =
												new Dictionary
												<TKey, TValue>()
										};
			_trustedSource = new Dictionary<TKey, TValue>();
			_requestsQueue = new LockFreeQueue<AsyncCollectionRequest<KeyValuePair<TKey, TValue>>>();
			_timer.Elapsed += OnSynchronize;
			_timer.Start();
		}

		public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
		{
			var data = _dictionary.Data;
			var keys = new List<TKey>(data.Keys);
			return new AsyncDictionaryLockFreeEnumerator<TKey,TValue>(keys, this);
		}

		IEnumerator IEnumerable.GetEnumerator()
		{
			return GetEnumerator();
		}

		public void TryAdd(KeyValuePair<TKey, TValue> item,
			Func<KeyValuePair<TKey, TValue>, KeyValuePair<TKey, TValue>, KeyValuePair<TKey, TValue>> tryFunction = null)
		{
			if (item.Key.Equals(default(TKey))) throw new ArgumentException("Item key cannot be null");
			_requestsQueue.Enqueue(new AsyncCollectionRequest<KeyValuePair<TKey, TValue>>
													 {
														 Data = item,
														 EventType = AsyncCollectionEventType.TryAdd,
														 TryFunc = tryFunction
													 });
		}

		public void Add(KeyValuePair<TKey, TValue> item)
		{
			TryAdd(item);
		}

		public void Clear()
		{
			_requestsQueue.Enqueue(new AsyncCollectionRequest<KeyValuePair<TKey, TValue>>
													 {
														 Data = null,
														 EventType = AsyncCollectionEventType.TryAdd,
														 TryFunc = null
													 });
		}

		public bool Contains(KeyValuePair<TKey, TValue> item)
		{
			return _dictionary.Data.Contains(item);
		}

		public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
		{
			((ICollection<KeyValuePair<TKey, TValue>>)_dictionary.Data).CopyTo(array, arrayIndex);
		}

		public bool Remove(KeyValuePair<TKey, TValue> item)
		{
			return Remove(item.Key);
		}

		public int Count
		{
			get
			{
				return _dictionary.Data.Count;
			}
		}

		public bool IsReadOnly { get { return false; } }

		public bool ContainsKey(TKey key)
		{
			return _dictionary.Data.ContainsKey(key);
		}

		public void Add(TKey key, TValue value)
		{
			Add(new KeyValuePair<TKey, TValue>(key, value));
		}

		public void TryAdd(TKey key, TValue value,
			Func<KeyValuePair<TKey, TValue>, KeyValuePair<TKey, TValue>, KeyValuePair<TKey, TValue>> tryFunction = null)
		{
			TryAdd(new KeyValuePair<TKey, TValue>(key, value), tryFunction);
		}

		public bool Remove(TKey key)
		{
			_requestsQueue.Enqueue(new AsyncCollectionRequest<KeyValuePair<TKey, TValue>>
			{
				Data = key,
				EventType = AsyncCollectionEventType.Remove,
				TryFunc = null
			});
			return true;
		}

		public bool TryGetValue(TKey key, out TValue value)
		{
			value = default(TValue);
			try
			{
				var dict = _dictionary.Data;
				if (!dict.ContainsKey(key)) return false;
				value = dict[key];
				return true;
			}
			catch (Exception)
			{
			}
			return false;
		}

		public TValue this[TKey key]
		{
			get
			{
				var dict = _dictionary.Data;
				return dict[key];
			}
			set
			{
				Add(key, value);
			}
		}

		public ICollection<TKey> Keys
		{
			get
			{
				var dict = _dictionary.Data;
				return new List<TKey>(dict.Keys);
			}
		}

		public ICollection<TValue> Values
		{
			get
			{
				var dict = _dictionary.Data;
				return new List<TValue>(dict.Values);
			}
		}

		private void OnSynchronize(object sender, ElapsedTimerEventArgs e)
		{
			var changesMade = false;
			if (1 == (int)_opverlappingChecker) return;
			_opverlappingChecker = 1;
			try
			{
				var maxMessages = (int)MaxMessagesPerCycle;
				foreach (var request in _requestsQueue.Dequeue(100))
				{
					if (HandledEvent(request))
					{
						changesMade = true;
					}
					maxMessages--;
					if (maxMessages <= 0) break;
				}
				if (changesMade)
				{
					var newData = new Dictionary<TKey, TValue>(_trustedSource);
					_dictionary.Data = newData;
				}
			}
			finally
			{
				_opverlappingChecker = 0;
			}
		}

		private bool HandledEvent(AsyncCollectionRequest<KeyValuePair<TKey, TValue>> request)
		{
			var changesMade = false;
			if (request.EventType == AsyncCollectionEventType.Clear)
			{
				changesMade = true;
				_trustedSource.Clear();
				// ReSharper disable once ConditionIsAlwaysTrueOrFalse
				return changesMade;
			}

			switch (request.EventType)
			{
				case (AsyncCollectionEventType.Remove):
					if (_trustedSource.ContainsKey((TKey)request.Data))
					{
						changesMade = true;
						_trustedSource.Remove((TKey)request.Data);
					}
					break;
				case (AsyncCollectionEventType.TryUpdate):
				case (AsyncCollectionEventType.TryAdd):
					{
						var kvp = (KeyValuePair<TKey, TValue>)request.Data;
						if (!_trustedSource.ContainsKey(kvp.Key))
						{
							if (AsyncCollectionEventType.TryAdd == request.EventType)
							{
								changesMade = true;
								_trustedSource.Add(kvp.Key, kvp.Value);
							}
						}
						else
						{
							changesMade = true;
							if (request.TryFunc == null)
							{
								_trustedSource[kvp.Key] = kvp.Value;
							}
							else
							{
								var currentValue = _trustedSource[kvp.Key];
								var selectedValue = request.TryFunc(new KeyValuePair<TKey, TValue>(kvp.Key, currentValue), kvp);
								_trustedSource[kvp.Key] = selectedValue.Value;
							}
						}
					}
					break;
			}
			return changesMade;
		}

		~AsyncLockFreeDictionary()
		{
			// Finalizer calls Dispose(false)
			Dispose(false);
		}

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		protected virtual void Dispose(bool disposing)
		{
			if (disposing)
			{
				// free managed resources
				if (_timer != null)
				{
					_timer.Dispose();
				}
				if (_dictionary != null)
				{
					_dictionary.Dispose();
				}
			}
		}
	}
}
