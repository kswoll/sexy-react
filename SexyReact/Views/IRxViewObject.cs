﻿using System;
using System.Linq.Expressions;
using System.Reflection;

namespace SexyReact.Views
{
    public interface IRxViewObject<T> : IRxObject
        where T : IRxObject
    {
        T Model { get; set; }
    }
}

