﻿namespace Ceras
{
	using Ceras.Exceptions;
	using Ceras.Formatters;
	using Helpers;
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Linq.Expressions;
	using System.Reflection;
	using System.Runtime.CompilerServices;


	// Serialization Constructors:
	// todo: exception: "your Type ... has no default ctor; you can activate an automatic fallback to 'GetUninitializedObject' if you want"
	// todo: allow the worst case scenario (self ref with ctor) by using GetUninitializedObject, then reading and assigning, and then running the ctor after that!
	// todo: "You have a configuration error on type 'bla': You selected 'mode' to create new objects, but the formatter used by ceras to handle this type is not 'DynamicObjectFormatter' which is required for this mode to work. If this formatter is a custom (user-provided) formatter then you probably want to set the construction mode to either 'Null' so Ceras does not create an object and the formatter can handle it; or you can set it to 'Normal' so Ceras creates a new object instance using 'new()'. Generally: passing any arguments to functions can only work with the DynamicObjectFormatter."
	// todo: Compile some sort of 'EnsureInstance()' method that we can call for each specific type and can use directly in the ref-formatter-dispatcher.
	// todo: when deferring to the dynamic object formatter, everything has to be done there: checking if an instance already exists, if it's the right type, and possibly discard it using the discard method

	// Extra Features:
	// todo: Methods for: BeforeReadingMember, BeforeWritingMember, AfterReadingMember, ... BeforeReadingObject, AfterReadingObject, ...
	// todo: DiscardObject method
	// todo: CustomSchema (with a method to obtain a default schema given some settings)
	// todo: If we create something from uninitialized; do we give an option to run some specific ctor? Do we write some props/fields again after calling the ctor??
	// todo: what about just calling Dispose() as an alternative to Discard!?

	public abstract class TypeConfig
	{
		public Type Type { get; }
		public SerializerConfig Config { get; }

		bool _isSealed = false;


		//
		// Settings
		protected TypeConstruction _typeConstruction; // null = invalid
		public TypeConstruction TypeConstruction
		{
			get => _typeConstruction;
			set
			{
				ThrowIfSealed();
				_typeConstruction = value;
				if (value != null)
				{
					TypeConstruction.TypeConfig = this;
					TypeConstruction.VerifyReturnType();
				}
			}
		}
		internal Dictionary<ParameterInfo, MemberInfo> ParameterMap;


		protected FormatterResolverCallback _customResolver;
		public FormatterResolverCallback CustomResolver
		{
			get => _customResolver;
			set
			{
				ThrowIfSealed();
				if (_overrideFormatter != null)
					ThrowCantSetResolverAndFormatter();
				_customResolver = value;
			}
		}

		protected IFormatter _overrideFormatter;
		public IFormatter CustomFormatter
		{
			get => _overrideFormatter;
			set
			{
				ThrowIfSealed();
				if (_customResolver != null)
					ThrowCantSetResolverAndFormatter();

				if (value != null)
				{
					FormatterHelper.ThrowOnMismatch(value, Type);
					_overrideFormatter = value;
				}
				else
				{
					_overrideFormatter = null;
				}
			}
		}

		void ThrowCantSetResolverAndFormatter() => throw new InvalidOperationException("You can only set a custom resolver or a custom formatter instance, not both.");


		protected ReadonlyFieldHandling? _readonlyOverride; // null = use global default from config
		public ReadonlyFieldHandling? ReadonlyFieldOverride
		{
			set
			{
				ThrowIfSealed();
				_readonlyOverride = value;
			}

			get => _readonlyOverride;
		}

		protected TargetMember? _targetMembers;
		public TargetMember? TargetMembers
		{
			set
			{
				ThrowIfSealed();
				_targetMembers = value;
			}

			get => _targetMembers;
		}


		internal readonly List<MemberConfig> _allMembers;
		public IEnumerable<MemberConfig> Members => _allMembers.Where(m => !m.IsCompilerGenerated);


		protected TypeConfig(SerializerConfig config, Type type)
		{
			Config = config;
			Type = type;

			var configType = typeof(MemberConfig<>).MakeGenericType(type);

			var members = from m in type.GetAllDataMembers()
						  let a = new object[] { this, m }
						  select (MemberConfig)Activator.CreateInstance(configType,
																	  BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
																	  null,
																	  a,
																	  null);

			_allMembers = members.ToList();


			TypeConfigDefaults.ApplyTypeAttributes(this);
			TypeConfigDefaults.ApplySpecializedDefaults(this);

			foreach (var m in _allMembers)
				TypeConfigDefaults.ApplyMemberAttributes(m);
		}

		/// <summary>
		/// Don't use this.
		/// </summary>
		public IEnumerable<MemberConfig> UnsafeGetAllMembersIncludingCompilerGenerated()
		{
			return _allMembers;
		}



		internal void Seal()
		{
			_isSealed = true;
		}

		internal void VerifyConstructionMethod()
		{
			if (TypeConstruction == null)
				throw new CerasException($"You have not configured a construction mode for the type '{Type.FullName}' and it has no parameterless constructor. There are many ways Ceras can handle this, select one of the methods in the TypeConfig ('config.ConfigType<YourType>().ConstructBy(...)')");

			TypeConstruction.VerifyReturnType();
			TypeConstruction.VerifyParameterMapping();
		}


		internal void ThrowIfSealed()
		{
			if (_isSealed)
				throw new ConfigurationSealedException("The configuration for this Type or Member is already sealed because the SerializationSchema has been instantiated (which means dynamically emitted code relies on it not changing anymore). All changes to the type or member configuration must be made before the the configuration is used in a 'CerasSerializer' instance, except for config callbacks like OnConfigNewType)");
		}
	}


	/// <inheritdoc/>
	public class TypeConfig<T> : TypeConfig
	{
		internal TypeConfig(SerializerConfig config) : base(config, typeof(T)) { }

		#region Construction

		/// <summary>
		/// Don't construct an instance for this object, let the formatter handle it (must be used together with a custom formatter to work)
		/// </summary>
		public TypeConfig<T> ConstructByFormatter()
		{
			TypeConstruction = TypeConstruction.Null();
			return this;
		}

		/// <summary>
		/// Call a given static method to get an object
		/// </summary>
		public TypeConfig<T> ConstructBy(MethodInfo methodInfo)
		{
			TypeConstruction = new ConstructByMethod(methodInfo);
			return this;
		}

		/// <summary>
		/// Call a given instance-method on the given object instance to create a object
		/// </summary>
		public TypeConfig<T> ConstructBy(object instance, MethodInfo methodInfo)
		{
			TypeConstruction = new ConstructByMethod(instance, methodInfo);
			return this;
		}

		/// <summary>
		/// Use the given constructor to create a new object
		/// </summary>
		public TypeConfig<T> ConstructBy(ConstructorInfo constructorInfo)
		{
			if (constructorInfo.IsAbstract || constructorInfo.DeclaringType != Type)
				throw new InvalidOperationException("This constructor does not belong to the type " + Type.FullName);

			TypeConstruction = new SpecificConstructor(constructorInfo);
			return this;
		}

		/// <summary>
		/// Call the given delegate to produce an object (this is the only method that currently does not support arguments, support for that will be added later)
		/// </summary>
		public TypeConfig<T> ConstructByDelegate(Func<T> factory)
		{
			// Delegates get deconstructed into target+method automatically
			var instance = factory.Target;
			var method = factory.Method;

			TypeConstruction = new ConstructByMethod(instance, method);
			return this;
		}

		/// <summary>
		/// Use the given static method (inferred from the given expression). This works exactly the same as <see cref="ConstructBy(MethodInfo)"/> but since it takes an Expression selecting a method is much easier (no need to fiddle around with reflection manually). The given expression is not compiled or called in any way.
		/// </summary>
		public TypeConfig<T> ConstructBy(Expression<Func<T>> methodSelectExpression)
		{
			return ConstructBy(instance: null, methodSelectExpression);
		}

		/// <summary>
		/// Use the given static method (inferred from the given expression). This works exactly the same as <see cref="ConstructBy(object, MethodInfo)"/> but since it takes an Expression selecting a method is much easier (no need to fiddle around with reflection manually). The given expression is not compiled or called in any way.
		/// </summary>
		public TypeConfig<T> ConstructBy(object instance, Expression<Func<T>> methodSelectExpression)
		{
			var body = methodSelectExpression.Body;

			if (body is NewExpression newExpression)
			{
				if (instance != null)
					throw new InvalidOperationException("You can't specify a constructor and an instance at the same time");

				var ctor = newExpression.Constructor;
				TypeConstruction = new SpecificConstructor(ctor);
			}
			else if (body is MethodCallExpression methodCall)
			{
				if (instance == null)
					TypeConstruction = new ConstructByMethod(methodCall.Method);
				else
					TypeConstruction = new ConstructByMethod(instance, methodCall.Method);
			}
			else
			{
				throw new InvalidOperationException("The given expression must be a 'method-call' or 'new-expression'");
			}

			return this;
		}

		/// <summary>
		/// Use this to tell Ceras how it is supposed to construct new objects when deserializing. By default it will use the parameterless constructor (doesn't matter if public or private)
		/// </summary>
		public TypeConfig<T> ConstructBy(TypeConstruction manualConstructConfig)
		{
			TypeConstruction = manualConstructConfig;
			return this;
		}

		/// <summary>
		/// Create an object without running any of its constructors
		/// </summary>
		public TypeConfig<T> ConstructByUninitialized()
		{
			TypeConstruction = new UninitializedObject();
			return this;
		}

		/// <summary>
		/// <para>
		/// Ceras can automatically map what members to put into the parameters of the constructor or factory method, but sometimes (when the names don't match up) you have to manually specify it
		/// </para>
		/// Only use this overload if you really have to (for example a parameter that will go into a private field)
		/// </summary>
		public TypeConfig<T> MapParameters(Dictionary<ParameterInfo, MemberInfo> mapping)
		{
			if (ParameterMap != null)
				throw new InvalidOperationException("ParameterMapping is already set");
			if (_typeConstruction == null)
				throw new InvalidOperationException("You must set a type construction method before mapping parameters");

			// Copy
			ParameterMap = mapping.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
			return this;
		}

		#endregion

		#region Formatter

		/// <summary>
		/// Set a custom formatter that will get used to serialize this type
		/// </summary>
		public TypeConfig<T> SetFormatter(IFormatter<T> formatterInstance)
		{
			CustomFormatter = formatterInstance;
			return this;
		}

		#endregion

		#region Type Settings

		/// <summary>
		/// Configure how readonly fields are handled for this type
		/// </summary>
		public TypeConfig<T> SetReadonlyHandling(ReadonlyFieldHandling mode)
		{
			_readonlyOverride = mode;
			return this;
		}

		/// <summary>
		/// Configure what fields and properties are included by default
		/// </summary>
		public TypeConfig<T> SetTargetMembers(TargetMember targets)
		{
			_targetMembers = targets;
			return this;
		}

		#endregion

		#region Member Config

		public MemberConfig<T> ConfigMember<TMember>(Expression<Func<T, TMember>> selectMemberExpression)
		{
			var memberInfo = ((MemberExpression)selectMemberExpression.Body).Member;
			var memberConfig = Members.FirstOrDefault(m => m.Member == memberInfo);
			return (MemberConfig<T>)memberConfig;
		}

		public MemberConfig<T> ConfigField(string fieldName)
		{
			var memberConfig = Members.FirstOrDefault(m => m.Member is FieldInfo && m.Member.Name == fieldName);
			return (MemberConfig<T>)memberConfig;
		}

		public MemberConfig<T> ConfigProperty(string propName)
		{
			var memberConfig = Members.FirstOrDefault(m => m.Member is PropertyInfo && m.Member.Name == propName);
			return (MemberConfig<T>)memberConfig;
		}

		#endregion
	}


	public abstract class MemberConfig
	{
		public TypeConfig TypeConfig { get; }
		public Type DeclaringType => TypeConfig.Type;
		public MemberInfo Member { get; }
		public Type MemberType => Member is FieldInfo f ? f.FieldType : ((PropertyInfo)Member).PropertyType;


		string _persistentNameOverride;
		public string PersistentName
		{
			get => _persistentNameOverride ?? Member.Name;
			set
			{
				TypeConfig.ThrowIfSealed();
				_persistentNameOverride = value;
			}
		}

		/// <summary>
		/// True for everything the compiler automatically generates: hidden async-state-machines, automatic enumerator implementations, cached dynamic method dispatchers, ...
		/// </summary>
		public bool IsCompilerGenerated { get; }
		/// <summary>
		/// True if it's a field and it is declared as 'readonly'
		/// </summary>
		public bool IsReadonlyField { get; }
		/// <summary>
		/// True only for properties that literally have no setter:
		/// <para>Example 1: int Time => Environment.TickCount;</para>
		/// <para>Example 2: string Name { get; }</para>
		/// <para>
		/// Properties with a private setter are not "computed" (even if the setter is private, or hidden in a base-type)
		/// </para>
		/// </summary>
		public bool IsComputedProperty { get; }

		internal bool HasNonSerialized;

		protected ReadonlyFieldHandling? _readonlyOverride;
		public ReadonlyFieldHandling? ReadonlyFieldHandling
		{
			set
			{
				TypeConfig.ThrowIfSealed();
				_readonlyOverride = value;
			}

			get => _readonlyOverride;
		}


		string _explicitInclusionReason = "Error: no inclusion/exclusion override is set for this member";

		/// <summary>
		/// Tells you why (or why not!) this member got included in the serialization.
		/// </summary>
		public string IncludeExcludeReason => ComputeFinalInclusionResult(Member, true).Reason;


		SerializationOverride _serializationOverride = SerializationOverride.NoOverride;
		/// <summary>
		/// Contains the serialization override for this member. It is initially set from any member attributes (like Include/Exclude) or the [MemberConfig] attribute.
		/// If none of those attributes exist the override is unspecified (in which case the DefaultTargets setting in the serializer config is used)
		/// </summary>
		public SerializationOverride SerializationOverride
		{
			set => SetIncludeWithReason(value, "User has explicitly set member.SerializationOverride");
			get => _serializationOverride;
		}


		internal void ExcludeWithReason(string reason) => SetIncludeWithReason(SerializationOverride.ForceSkip, reason);

		internal void SetIncludeWithReason(SerializationOverride serializationOverride, string reason)
		{
			TypeConfig.ThrowIfSealed();
			if (serializationOverride == SerializationOverride.NoOverride)
				throw new InvalidOperationException("Explicitly setting 'NoOverride', this must be a bug, please report it on GitHub!");
			if (string.IsNullOrWhiteSpace(reason))
				throw new InvalidOperationException("Missing reason in " + nameof(SetIncludeWithReason) + ", this must be a bug, please report it on GitHub!");

			_serializationOverride = serializationOverride;
			_explicitInclusionReason = reason;
		}

		protected MemberConfig(TypeConfig typeConfig, MemberInfo member)
		{
			TypeConfig = typeConfig;
			Member = member;

			IsCompilerGenerated = member.GetCustomAttribute<CompilerGeneratedAttribute>() != null;
			IsReadonlyField = member is FieldInfo f && f.IsInitOnly;
			IsComputedProperty = member is PropertyInfo p && p.GetSetMethod(true) == null;
		}



		/// <summary>
		/// Determine wether or not this member is included for serialization
		/// </summary>
		/// <returns>true when the member will be serialized/deserialized</returns>
		public InclusionExclusionResult ComputeFinalInclusion() => ComputeFinalInclusionResult(Member, true);

		internal bool ComputeFinalInclusionFast() => ComputeFinalInclusionResult(Member, false).IsIncluded;


		InclusionExclusionResult ComputeFinalInclusionResult(MemberInfo memberInfo, bool needReason)
		{
			// [Include] / [Exclude]
			if (_serializationOverride != SerializationOverride.NoOverride)
				return new InclusionExclusionResult(_serializationOverride == SerializationOverride.ForceInclude, _explicitInclusionReason);

			// [NonSerialized]
			if (HasNonSerialized && TypeConfig.Config.Advanced.RespectNonSerializedAttribute)
				return new InclusionExclusionResult(false, $"Member has [NonSerializedAttribute] and 'RespectNonSerializedAttribute' is set in the config.");


			// Skip readonly fields
			if (IsReadonlyField)
			{
				if (_readonlyOverride.HasValue)
				{
					// Check attribute on member
					if (_readonlyOverride.Value == Ceras.ReadonlyFieldHandling.ExcludeFromSerialization)
						return new InclusionExclusionResult(false, "Field is readonly and has [ReadonlyConfig] set to exclude.");
				}
				else if (TypeConfig.ReadonlyFieldOverride.HasValue)
				{
					// Attribute on Type (MemberConfig)
					if (TypeConfig.ReadonlyFieldOverride.Value == Ceras.ReadonlyFieldHandling.ExcludeFromSerialization)
						return new InclusionExclusionResult(false, "Field is readonly and the declaring type has a [MemberConfig] which excludes it");
				}
				else
				{
					// Get global default from config
					if (TypeConfig.Config.Advanced.ReadonlyFieldHandling == Ceras.ReadonlyFieldHandling.ExcludeFromSerialization)
						return new InclusionExclusionResult(false, "Field is readonly and the global default in the SerializerConfig is set to exclude");
				}
			}

			// Calculate inclusion by "TargetMember" mask
			var requiredMask = ComputeMemberTargetMask(memberInfo);

			// Type [MemberConfig]
			if (TypeConfig.TargetMembers.HasValue)
			{
				if ((TypeConfig.TargetMembers.Value & requiredMask) != 0)
					return new InclusionExclusionResult(true, needReason
																? $"Member is '{requiredMask.Singular()}', which is included through the configuration '{TypeConfig.TargetMembers.Value}' of the declared Type '{DeclaringType.Name}'"
																: null);
				else
					return new InclusionExclusionResult(false, needReason
																? $"Member is '{requiredMask.Singular()}', which is excluded through the configuration '{TypeConfig.TargetMembers.Value}' of the declared Type '{DeclaringType.Name}'"
																: null);
			}

			// Global config
			var defaultTargets = TypeConfig.Config.DefaultTargets;

			if ((defaultTargets & requiredMask) != 0)
				return new InclusionExclusionResult(true, needReason
															? $"Member is '{requiredMask.Singular()}', which is included by the 'DefaultTargets' configuration in the SerializerConfig"
															: null);
			else
				return new InclusionExclusionResult(false, needReason
															? $"Member is '{requiredMask.Singular()}', which is excluded by the 'DefaultTargets' configuration in the SerializerConfig"
															: null);
		}

		static TargetMember ComputeMemberTargetMask(MemberInfo member)
		{
			if (member is FieldInfo f)
			{
				if (f.IsPublic)
					return TargetMember.PublicFields;
				else
					return TargetMember.PrivateFields;
			}
			else if (member is PropertyInfo p)
			{
				if (p.GetGetMethod(true).IsPublic)
					return TargetMember.PublicProperties;
				else
					return TargetMember.PrivateProperties;
			}
			else
				throw new ArgumentOutOfRangeException();
		}

		internal ReadonlyFieldHandling ComputeReadonlyHandling()
		{
			// Local?
			if (_readonlyOverride.HasValue)
				return _readonlyOverride.Value;

			// Type?
			if (TypeConfig.ReadonlyFieldOverride.HasValue)
				return TypeConfig.ReadonlyFieldOverride.Value;

			// Global
			return TypeConfig.Config.Advanced.ReadonlyFieldHandling;
		}
	}

	public struct InclusionExclusionResult
	{
		public readonly bool IsIncluded;
		public readonly string Reason;

		public InclusionExclusionResult(bool isIncluded, string reason)
		{
			IsIncluded = isIncluded;
			Reason = reason;
		}
	}

	public class MemberConfig<TDeclaring> : MemberConfig
	{
		public new TypeConfig<TDeclaring> TypeConfig => (TypeConfig<TDeclaring>)base.TypeConfig;

		public MemberConfig(TypeConfig typeConfig, MemberInfo member) : base(typeConfig, member) { }


		public TypeConfig<TDeclaring> SetReadonlyHandling(ReadonlyFieldHandling r)
		{
			_readonlyOverride = r;
			return TypeConfig;
		}

		public TypeConfig<TDeclaring> Include()
		{
			SetIncludeWithReason(SerializationOverride.ForceInclude, "User called Include()");
			return TypeConfig;
		}

		public TypeConfig<TDeclaring> Include(ReadonlyFieldHandling readonlyHandling)
		{
			ReadonlyFieldHandling = readonlyHandling;
			SetIncludeWithReason(SerializationOverride.ForceInclude, "User called Include()");
			return TypeConfig;
		}

		public TypeConfig<TDeclaring> Exclude() => Exclude("User called Exclude()");

		public TypeConfig<TDeclaring> Exclude(string customReason)
		{
			SetIncludeWithReason(SerializationOverride.ForceSkip, customReason);
			return TypeConfig;
		}
	}

	public struct TUnknown { }
}
