using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using UndertaleModLib;

namespace UndertaleModTool
{
    public class ChangeTracker : IDisposable
    {
        private readonly HashSet<UndertaleResource> _modifiedResources = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<INotifyPropertyChanged, UndertaleResource> _trackedObjects = new(ReferenceEqualityComparer.Instance);
        private readonly List<INotifyCollectionChanged> _trackedCollections = new();
        private UndertaleData _data;
        private bool _disposed;

        public IReadOnlySet<UndertaleResource> ModifiedResources => _modifiedResources;
        public bool HasModifications => _modifiedResources.Count > 0;

        public event EventHandler ModifiedChanged;

        public void Initialize(UndertaleData data)
        {
            if (_data != null)
                Dispose();

            _data = data;
            _modifiedResources.Clear();
            _trackedObjects.Clear();
            _trackedCollections.Clear();

            if (data == null)
                return;

            foreach (var prop in data.AllListProperties)
            {
                if (prop.Name == "Item")
                    continue;

                if (prop.GetValue(data) is not IList list)
                    continue;

                TrackCollection(list);

                foreach (var item in list)
                {
                    if (item is UndertaleResource resource)
                        TrackResource(resource);
                }
            }
        }

        private void TrackCollection(IList list)
        {
            if (list is INotifyCollectionChanged ncc)
            {
                ncc.CollectionChanged += OnCollectionChanged;
                _trackedCollections.Add(ncc);
            }
        }

        private void TrackResource(UndertaleResource resource)
        {
            if (resource is INotifyPropertyChanged npc)
            {
                if (!_trackedObjects.ContainsKey(npc))
                {
                    _trackedObjects[npc] = resource;
                    npc.PropertyChanged += OnObjectPropertyChanged;
                }
            }
        }

        private void OnObjectPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (sender is INotifyPropertyChanged npc && _trackedObjects.TryGetValue(npc, out var resource))
            {
                MarkModified(resource);
            }
        }

        private void OnCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (var item in e.NewItems)
                {
                    if (item is UndertaleResource resource)
                    {
                        TrackResource(resource);
                        MarkModified(resource);
                    }
                }
            }

            if (e.OldItems != null)
            {
                foreach (var item in e.OldItems)
                {
                    if (item is INotifyPropertyChanged npc && _trackedObjects.TryGetValue(npc, out var resource))
                    {
                        npc.PropertyChanged -= OnObjectPropertyChanged;
                        _trackedObjects.Remove(npc);
                        MarkModified(resource);
                    }
                }
            }
        }

        public void MarkModified(UndertaleResource resource)
        {
            if (resource == null)
                return;

            if (!(Settings.Instance?.ChangeTrackingEnabled ?? true))
                return;

            if (_modifiedResources.Add(resource))
                ModifiedChanged?.Invoke(this, EventArgs.Empty);
        }

        public void UnmarkModified(UndertaleResource resource)
        {
            if (resource == null)
                return;

            if (_modifiedResources.Remove(resource))
                ModifiedChanged?.Invoke(this, EventArgs.Empty);
        }

        public bool IsModified(UndertaleResource resource)
        {
            return resource != null && _modifiedResources.Contains(resource);
        }

        public void ClearAll()
        {
            if (_modifiedResources.Count > 0)
            {
                _modifiedResources.Clear();
                ModifiedChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            foreach (var npc in _trackedObjects.Keys)
                npc.PropertyChanged -= OnObjectPropertyChanged;

            foreach (var ncc in _trackedCollections)
                ncc.CollectionChanged -= OnCollectionChanged;

            _trackedObjects.Clear();
            _trackedCollections.Clear();
            _modifiedResources.Clear();
            _data = null;
            _disposed = true;
        }

        private class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new();

            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
