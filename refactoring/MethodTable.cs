﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using dnlib.DotNet;

namespace il2cpp
{
	internal class TypeDefGenReplacer : IGenericReplacer
	{
		public readonly TypeDef OwnerType;
		public readonly IList<TypeSig> TypeGenArgs;

		public TypeDefGenReplacer(TypeDef ownerTyDef, IList<TypeSig> tyGenArgs)
		{
			OwnerType = ownerTyDef;
			TypeGenArgs = tyGenArgs;
		}

		public TypeSig Replace(GenericVar genVarSig)
		{
			if (TypeEqualityComparer.Instance.Equals(genVarSig.OwnerType, OwnerType))
				return TypeGenArgs[(int)genVarSig.Number];
			return null;
		}

		public TypeSig Replace(GenericMVar genMVarSig)
		{
			return null;
		}
	}

	internal struct VirtualImpl
	{
		public MethodTable ImplTable;
		public MethodDef ImplMethod;

		public override string ToString()
		{
			return ImplTable + " : " + ImplMethod;
		}

		public bool IsValid()
		{
			return ImplTable != null && ImplMethod != null;
		}
	}

	internal class VirtualSlot
	{
		// 虚槽入口集合, 为具体的类型和方法的映射
		public readonly Dictionary<MethodTable, MethodDef> Entries = new Dictionary<MethodTable, MethodDef>();
		// 实现方法
		public VirtualImpl Impl;

		public VirtualSlot()
		{
		}

		public VirtualSlot(VirtualSlot other)
		{
			foreach (var kv in other.Entries)
			{
				Entries.Add(kv.Key, kv.Value);
			}

			Impl = other.Impl;
		}

		public void AddEntry(MethodTable entryTable, MethodDef entryDef)
		{
			Entries.Add(entryTable, entryDef);
		}

		public void SetImpl(MethodTable mtable, MethodDef metDef)
		{
			Impl.ImplTable = mtable;
			Impl.ImplMethod = metDef;
		}
	}

	internal class MethodTable : GenericArgs
	{
		private readonly Il2cppContext Context;
		private readonly TypeDef Def;
		private string NameKey;

		// 实例方法列表
		private readonly List<MethodDef> MethodDefList = new List<MethodDef>();
		// 原始方法签名列表
		private readonly List<string> OrigSigList = new List<string>();
		// 展开类型泛型的方法签名列表
		private List<string> ExpandedSigList = new List<string>();

		// 虚表槽映射
		private readonly Dictionary<string, VirtualSlot> VSlotMap = new Dictionary<string, VirtualSlot>();

		// 展平的虚表
		private readonly Dictionary<MethodTable, Dictionary<MethodDef, VirtualImpl>> VTable =
			new Dictionary<MethodTable, Dictionary<MethodDef, VirtualImpl>>();

		internal MethodTable(Il2cppContext context, TypeDef tyDef)
		{
			Context = context;
			Def = tyDef;
		}

		public override string ToString()
		{
			return NameKey;
		}

		public string GetNameKey()
		{
			if (NameKey == null)
			{
				StringBuilder sb = new StringBuilder();
				NameManager.TypeNameKey(sb, Def.FullName, GenArgs, true);
				NameKey = sb.ToString();
			}
			return NameKey;
		}

		public void ResolveTable()
		{
			StringBuilder sb = new StringBuilder();

			IGenericReplacer replacer = null;
			if (HasGenArgs)
				replacer = new TypeDefGenReplacer(Def, GenArgs);

			// 解析不展开类型泛型的方法签名, 和展开的方法签名
			foreach (var metDef in Def.Methods)
			{
				if (metDef.IsStatic || metDef.IsConstructor)
					continue;

				MethodDefList.Add(metDef);

				NameManager.MethodDefName(
					sb,
					metDef.Name,
					metDef.GenericParameters,
					metDef.MethodSig.RetType,
					metDef.MethodSig.Params,
					metDef.MethodSig.CallingConvention);
				OrigSigList.Add(sb.ToString());
				sb.Clear();

				// 如果存在替换器则生成替换的方法签名
				if (replacer != null)
				{
					TypeSig retType = TypeManager.ReplaceGenericSig(metDef.MethodSig.RetType, replacer);
					IList<TypeSig> paramTypes = TypeManager.ReplaceGenericSigList(metDef.MethodSig.Params, replacer);

					NameManager.MethodDefName(
						sb,
						metDef.Name,
						metDef.GenericParameters,
						retType,
						paramTypes,
						metDef.MethodSig.CallingConvention);
					ExpandedSigList.Add(sb.ToString());
					sb.Clear();
				}
			}

			// 如果没有可替换的泛型参数则引用原始的方法签名
			if (replacer == null)
				ExpandedSigList = OrigSigList;

			// 解析并继承基类方法表
			if (Def.BaseType != null)
			{
				MethodTable baseTable = Context.TypeMgr.ResolveMethodTable(Def.BaseType, replacer);
				DerivedFrom(baseTable);
			}

			List<int> explicitOverrides = new List<int>();

			// 处理当前类型的覆盖
			for (int i = 0; i < MethodDefList.Count; ++i)
			{
				MethodDef metDef = MethodDefList[i];
				string expSigName = ExpandedSigList[i];

				if (metDef.HasOverrides)
				{
					// 记录存在显式覆盖的方法
					explicitOverrides.Add(i);
				}

				if (metDef.IsVirtual)
				{
					if (metDef.IsNewSlot)
					{
						NewSlot(expSigName, metDef);
					}
					else
					{
						Debug.Assert(metDef.IsReuseSlot);
						ReuseSlot(expSigName, metDef);
					}
				}
				else
				{
					NewSlot(expSigName, metDef, true);
				}
			}

			// 合并接口的虚表槽到当前虚表槽
			if (Def.HasInterfaces)
			{
				foreach (var inf in Def.Interfaces)
				{
					MethodTable infTable = Context.TypeMgr.ResolveMethodTable(inf.Interface, replacer);

					foreach (var kv in infTable.VSlotMap)
					{
						string expSigName = kv.Key;
						VirtualSlot vslot = kv.Value;
						foreach (var kv2 in vslot.Entries)
						{
							MethodTable entryTable = kv2.Key;
							MethodDef entryDef = kv2.Value;
							MergeSlot(expSigName, entryTable, entryDef);
						}
					}
				}
			}

			// 处理显式覆盖
			foreach (int idx in explicitOverrides)
			{
				MethodDef overDef = MethodDefList[idx];
				ExplicitOverride(overDef.Overrides, overDef, idx);
			}

			// 展平虚表
			foreach (VirtualSlot vslot in VSlotMap.Values)
			{
				foreach (var kv in vslot.Entries)
				{
					MethodTable entryTable = kv.Key;
					MethodDef entryDef = kv.Value;

					if (!Def.IsInterface && !vslot.Impl.IsValid())
						throw new TypeLoadException("Slot has no implementation: " + entryTable + entryDef.FullName);

					SetVTable(entryTable, entryDef, ref vslot.Impl);
				}
			}
		}

		private void DerivedFrom(MethodTable other)
		{
			foreach (var kv in other.VSlotMap)
			{
				VSlotMap.Add(kv.Key, new VirtualSlot(kv.Value));
			}

			foreach (var kv in other.VTable)
			{
				VTable.Add(kv.Key, new Dictionary<MethodDef, VirtualImpl>(kv.Value));
			}
		}

		private void SetVTable(MethodTable entryTable, MethodDef entryDef, ref VirtualImpl impl)
		{
			if (!VTable.TryGetValue(entryTable, out var implMap))
			{
				implMap = new Dictionary<MethodDef, VirtualImpl>();
				VTable.Add(entryTable, implMap);
			}
			implMap[entryDef] = impl;
		}

		private static string GetModifiedSigName(string expSigName, MethodDef metDef)
		{
			// 对于终止覆盖方法或者私有方法, 在签名前面加上标记以防止后续覆盖
			if (metDef.IsFinal || metDef.IsPrivate)
			{
				return "*|" + expSigName;
			}
			return expSigName;
		}

		private void NewSlot(string expSigName, MethodDef metDef, bool isChecked = false)
		{
			expSigName = GetModifiedSigName(expSigName, metDef);

			VirtualSlot vslot = new VirtualSlot();
			if (isChecked)
				VSlotMap.Add(expSigName, vslot);
			else
				VSlotMap[expSigName] = vslot;
			vslot.AddEntry(this, metDef);
			vslot.SetImpl(this, metDef);
		}

		private void ReuseSlot(string expSigName, MethodDef metDef)
		{
			bool status = VSlotMap.TryGetValue(expSigName, out var vslot);
			Debug.Assert(status);
			vslot.AddEntry(this, metDef);
			vslot.SetImpl(this, metDef);
		}

		private void MergeSlot(string expSigName, MethodTable entryTable, MethodDef entryDef)
		{
			if (!VSlotMap.TryGetValue(expSigName, out var vslot))
			{
				vslot = new VirtualSlot();
				VSlotMap.Add(expSigName, vslot);
			}
			vslot.AddEntry(entryTable, entryDef);
		}

		private void ExplicitOverride(IList<MethodOverride> overList, MethodDef ownerMetDef, int ownerMetIdx)
		{
			IGenericReplacer replacer = null;
			if (HasGenArgs)
				replacer = new TypeDefGenReplacer(Def, GenArgs);

			foreach (var overItem in overList)
			{
				var target = overItem.MethodDeclaration;
				var impl = overItem.MethodBody;

				MethodTable targetTable = Context.TypeMgr.ResolveMethodTable(target.DeclaringType, replacer);
				MethodDef targetDef = target.ResolveMethodDef();

				MethodDef implDef = impl.ResolveMethodDef();
				string expSigName = null;

				if (implDef == ownerMetDef)
				{
					//Debug.Assert(implTable == this);
					expSigName = ExpandedSigList[ownerMetIdx];
					expSigName = GetModifiedSigName(expSigName, implDef);
				}
				else
					throw new NotSupportedException();

				// 删除现有的覆盖目标入口
				RemoveEntry(targetTable, targetDef);

				// 合并目标入口到实现方法的虚表槽内
				MergeSlot(expSigName, targetTable, targetDef);
			}
		}

		private void RemoveEntry(MethodTable entryTable, MethodDef entryDef)
		{
			// 在所有虚表槽内查找入口
			foreach (VirtualSlot vslot in VSlotMap.Values)
			{
				// 找到匹配的入口则删除
				if (vslot.Entries.TryGetValue(entryTable, out var oDef) &&
					oDef == entryDef)
				{
					vslot.Entries.Remove(entryTable);
				}
			}
		}
	}
}
