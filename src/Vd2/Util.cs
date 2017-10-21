﻿using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Vd2
{
    internal static class Util
    {
        [DebuggerNonUserCode]
        internal static TDerived AssertSubtype<TBase, TDerived>(TBase value) where TDerived : class, TBase where TBase : class
        {
            if (value == null)
            {
                return null;
            }

            if (!(value is TDerived derived))
            {
                throw new VdException($"object {value} must be derived type {typeof(TDerived).FullName} to be used in this context.");
            }

            return derived;
        }

        internal static void EnsureArraySize<T>(ref T[] array, uint size)
        {
            if (array.Length < size)
            {
                Array.Resize(ref array, (int)size);
            }
        }

        internal static uint USizeOf<T>() where T : struct
        {
            return (uint)Unsafe.SizeOf<T>();
        }

        internal static unsafe string GetString(byte* stringStart)
        {
            int characters = 0;
            while (stringStart[characters] != 0)
            {
                characters++;
            }

            return Encoding.UTF8.GetString(stringStart, characters);
        }

        internal static bool ArrayEquals<T>(T[] left, T[] right) where T : class
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            for (int i = 0; i < left.Length; i++)
            {
                if (!ReferenceEquals(left[i], right[i]))
                {
                    return false;
                }
            }

            return true;
        }

        internal static bool ArrayEqualsBlittable<T>(T[] left, T[] right) where T : struct, IEquatable<T>
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            for (int i = 0; i < left.Length; i++)
            {
                if (!left[i].Equals(right[i]))
                {
                    return false;
                }
            }

            return true;
        }
    }
}