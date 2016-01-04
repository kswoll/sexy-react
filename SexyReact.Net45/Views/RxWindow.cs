﻿using System;
using System.Reflection;
using System.Windows;

namespace SexyReact.Views
{
    public class RxWindow<T> : Window, IRxViewObject<T> 
        where T : IRxObject
    {
        private IRxViewObject<T> mixin = new RxViewObject<T>();
        private bool isDisposed;

        static RxWindow()
        {
            RxWpf.RegisterDependency();
        }

        public RxWindow()
        {
            Unloaded += (sender, args) => Dispose();
        }

        public void Register(IDisposable disposable) => mixin.Register(disposable);
        public TValue Get<TValue>(PropertyInfo property) => mixin.Get<TValue>(property);
        public void Set<TValue>(PropertyInfo property, TValue value) => mixin.Set(property, value);
        public IObservable<TValue> ObserveProperty<TValue>(PropertyInfo property) => mixin.ObserveProperty<TValue>(property);
        public IObservable<IPropertyChanging> Changing => mixin.Changing;
        public IObservable<IPropertyChanged> Changed => mixin.Changed;

        object IRxViewObject.Model
        {
            get { return Model; }
            set { Model = (T)value; }
        }

        public T Model
        {
            get { return mixin.Model; }
            set
            {
                mixin.Model = value;
                DataContext = value;
            }
        }

        public void Dispose()
        {
            if (!isDisposed)
            {
                isDisposed = true;
                Dispose(true);
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                mixin.Dispose();
            }
        }
    }
}