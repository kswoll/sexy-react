﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reactive.Subjects;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace SexyReact
{
    public class RxObject : IRxObject, INotifyPropertyChanged, INotifyPropertyChanging
    {
        private IStorageStrategy storageStrategy;
        private Lazy<List<IDisposable>> disposables = new Lazy<List<IDisposable>>();
        private IObservePropertyStrategy observePropertyStrategy;
        private Lazy<Subject<IPropertyChanged>> changed = new Lazy<Subject<IPropertyChanged>>(() => new Subject<IPropertyChanged>());
        private Lazy<Subject<IPropertyChanging>> changing = new Lazy<Subject<IPropertyChanging>>(() => new Subject<IPropertyChanging>());
        private Lazy<ConcurrentDictionary<PropertyInfo, Subject<IPropertyChanged>>> changedByProperty = new Lazy<ConcurrentDictionary<PropertyInfo, Subject<IPropertyChanged>>>();
        private Lazy<ConcurrentDictionary<PropertyInfo, Subject<IPropertyChanging>>> changingByProperty = new Lazy<ConcurrentDictionary<PropertyInfo, Subject<IPropertyChanging>>>();
        private bool disposed;
        private PropertyChangedEventHandler propertyChanged;
        private PropertyChangingEventHandler propertyChanging;

        public RxObject()
        {
            storageStrategy = new DictionaryStorageStrategy();
            observePropertyStrategy = new DictionaryObservePropertyStrategy(this);
        }

        public RxObject(IStorageStrategy storageStrategy, IObservePropertyStrategy observePropertyStrategy)
        {
            this.storageStrategy = storageStrategy;
            this.observePropertyStrategy = observePropertyStrategy;
        }

        public IObservable<IPropertyChanging> Changing => changing.Value;
        public IObservable<IPropertyChanged> Changed => changed.Value;
        TValue IRxObject.Get<TValue>(PropertyInfo property) => Get<TValue>(property);
        void IRxObject.Set<TValue>(PropertyInfo property, TValue value) => Set(property, value);

        public IObservable<IPropertyChanged> GetChangedByProperty(PropertyInfo property)
        {
            return changedByProperty.Value.GetOrAdd(property, _ => new Subject<IPropertyChanged>());
        }

        public IObservable<IPropertyChanging> GetChangingByProperty(PropertyInfo property)
        {
            return changingByProperty.Value.GetOrAdd(property, _ => new Subject<IPropertyChanging>());
        }

        /// <summary>
        /// Gets the current value of the property as returned by the IStorageStrategy.
        /// </summary>
        /// <typeparam name="TValue">The type of the property</typeparam>
        /// <param name="property">The PropertyInfo corresponding to the property defined on your type for which
        /// you are attempting to get the current value.</param>
        /// <returns>The current value of the property</returns>
        protected TValue Get<TValue>(PropertyInfo property)
        {
            return storageStrategy.Retrieve<TValue>(property);
        }

        /// <summary>
        /// Gets the current value of the specified property.  This method should be called by your property getter 
        /// implementations.  This method uses reflection and string lookup of the PropertyInfo, so will inevitably 
        /// be slower than if you were to store the PropertyInfo yourself and pass it in directly to the other overload.
        /// </summary>
        /// <typeparam name="TValue">The type of the property</typeparam>
        /// <param name="propertyName">The name of the property.  You may omit this and the compiler will generate it for you. 
        /// (presuming, of course, that you are in fact calling it from your property getter)</param>
        /// <returns>The current value of the property.</returns>
        protected TValue Get<TValue>([CallerMemberName]string propertyName = null)
        {
            if (propertyName == null)
                throw new ArgumentNullException(nameof(propertyName));

            var propertyInfo = GetType().GetProperty(propertyName);
            if (propertyInfo == null)
                throw new ArgumentException("Property not found", nameof(propertyName));

            return Get<TValue>(propertyInfo);
        }

        /// <summary>
        /// Sets the value of the specified property to newValue by delegating the call to the IStorageStrategy.  
        /// Furthermore, will propagate changing and changed notifications.  The new value may be modified by 
        /// subscribers to the changing notification.
        /// </summary>
        /// <typeparam name="TValue">The type of the property</typeparam>
        /// <param name="property">The PropertyInfo corresponding to the property defined on your type for which
        /// you are attempting to set the current value.</param>
        /// <param name="newValue">The new value of the property</param>
        protected void Set<TValue>(PropertyInfo property, TValue newValue)
        {
            TValue oldValue = storageStrategy.Retrieve<TValue>(property);
            if (!Equals(oldValue, newValue))
            {
                newValue = OnChanging(property, oldValue, newValue);
            
                storageStrategy.Store(property, newValue);

                OnChanged(property, oldValue, newValue);
            }
        }

        protected TValue OnChanging<TValue>(PropertyInfo property, TValue oldValue, TValue newValue)
        {
            var propertyChanging = new PropertyChanging<TValue>(property, oldValue, () => newValue, x => newValue = x);
            if (changing.IsValueCreated)
            {
                changing.Value.OnNext(propertyChanging);
            }
            if (changingByProperty.IsValueCreated)
            {
                Subject<IPropertyChanging> subject;
                if (changingByProperty.Value.TryGetValue(property, out subject)) 
                    subject.OnNext(propertyChanging);
            }
            this.propertyChanging?.Invoke(this, new PropertyChangingEventArgs(property.Name));
            return newValue;
        }

        protected void OnChanged<TValue>(PropertyInfo property, TValue oldValue, TValue newValue)
        {
            var propertyChanged = new PropertyChanged<TValue>(property, oldValue, newValue);
            if (changed.IsValueCreated)
            {
                changed.Value.OnNext(propertyChanged);
            }
            if (changedByProperty.IsValueCreated)
            {
                Subject<IPropertyChanged> subject;
                if (changedByProperty.Value.TryGetValue(property, out subject))
                    subject.OnNext(propertyChanged);
            }

            observePropertyStrategy.OnNext(property, newValue);                
            this.propertyChanged?.Invoke(this, new PropertyChangedEventArgs(property.Name));            
        }

        /// <summary>
        /// Sets the current value of the specified property.  This method should be called by your property setter 
        /// implementations.  This method uses reflection and string lookup of the PropertyInfo, so will inevitably 
        /// be slower than if you were to store the PropertyInfo yourself and pass it in directly to the other overload.
        /// </summary>
        /// <typeparam name="TValue">The type of the property</typeparam>
        /// <param name="newValue">The new value of the property</param>
        /// <param name="propertyName">The name of the property.  You may omit this and the compiler will generate it for you. 
        /// (presuming, of course, that you are in fact calling it from your property setter)</param>
        protected void Set<TValue>(TValue newValue, [CallerMemberName]string propertyName = null)
        {
            if (propertyName == null)
                throw new ArgumentNullException(nameof(propertyName));

            var propertyInfo = GetType().GetProperty(propertyName);
            if (propertyInfo == null)
                throw new ArgumentException("Property not found", nameof(propertyName));

            Set(propertyInfo, newValue);
        }

        /// <summary>
        /// Returns an observable that emits the current value of the specified property as it changes.  This observable
        /// will always immediately emit the current value of the property upon subscription.
        /// </summary>
        /// <typeparam name="TValue">The type of the property</typeparam>
        /// <param name="property">The property for which values should be observed.</param>
        /// <returns></returns>
        public IObservable<TValue> ObserveProperty<TValue>(PropertyInfo property)
        {
            return observePropertyStrategy.ObservableForProperty<TValue>(property);
        }

        public void Register(IDisposable disposable)
        {
            disposables.Value.Add(disposable);
        }

        public void Dispose()
        {
            if (!disposed)
            {
                disposed = true;
                Dispose(true);
            }
        }

        protected virtual void Dispose(bool isDisposing)
        {
            if (isDisposing)
            {
                if (changed.IsValueCreated)
                    changed.Value.Dispose();
                if (changing.IsValueCreated)
                    changing.Value.Dispose();
                storageStrategy.Dispose();
                observePropertyStrategy.Dispose();
                foreach (var disposable in disposables.Value)
                    disposable.Dispose();
                disposables.Value.Clear();
            }
        }

        event PropertyChangedEventHandler INotifyPropertyChanged.PropertyChanged
        {
            add { propertyChanged = (PropertyChangedEventHandler)Delegate.Combine(propertyChanged, value); }
            remove { propertyChanged = (PropertyChangedEventHandler)Delegate.Remove(propertyChanged, value); }
        }

        event PropertyChangingEventHandler INotifyPropertyChanging.PropertyChanging
        {
            add { propertyChanging = (PropertyChangingEventHandler)Delegate.Combine(propertyChanging, value); }
            remove { propertyChanging = (PropertyChangingEventHandler)Delegate.Remove(propertyChanging, value); }
        }
    }
}