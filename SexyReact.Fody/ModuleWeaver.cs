﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Fody;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using FieldAttributes = Mono.Cecil.FieldAttributes;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using MethodBody = Mono.Cecil.Cil.MethodBody;

namespace SexyReact.Fody
{
    public class ModuleWeaver : BaseModuleWeaver
    {
        public override IEnumerable<string> GetAssembliesForScanning()
        {
            return new[] { "netstandard", "mscorlib" };
        }

        /*
                private AssemblyNameReference FindSexyReactAssembly()
                {
                    var sexyReact = ModuleDefinition.FindAssembly("SexyReact");
                    if (sexyReact != null)
                        return sexyReact;

                    var assemblies = ModuleDefinition.AssemblyReferences.ToArray();
                    foreach (var assembly in assemblies)
                    {
                        var reactiveObject = new TypeReference("SexyReact", "IRxObject", ModuleDefinition, assembly);
                        if (reactiveObject.Resolve() != null)
                        {
                            return assembly;
                        }
                    }
                    return null;
                }
        */

        public override void Execute()
        {
            CecilExtensions.LogInfo = LogInfo;

            var sexyReact = ModuleDefinition.FindAssembly("SexyReact");
            if (sexyReact == null)
            {
                LogInfo("Could not find assembly: SexyReact (" + string.Join(", ", ModuleDefinition.AssemblyReferences.Select(x => x.Name)) + ")");
                return;
            }
            LogInfo($"{sexyReact.Name} {sexyReact.Version}");
            var reactiveObject = new TypeReference("SexyReact", "IRxObject", ModuleDefinition, sexyReact);
            var targetTypes = ModuleDefinition.GetAllTypes().Where(x => x.BaseType != null && reactiveObject.IsAssignableFrom(x.BaseType)).ToArray();
            var propertyInfoType = ModuleDefinition.Import(typeof(PropertyInfo));
//            LogInfo($"propertyInfoType: {propertyInfoType}");
            var getMethod = ModuleDefinition.Import(reactiveObject.Resolve().Methods.SingleOrDefault(x => x.Name == "Get"));
            if (getMethod == null)
                throw new Exception("getMethod is null");

            var setMethod = ModuleDefinition.Import(reactiveObject.Resolve().Methods.SingleOrDefault(x => x.Name == "Set"));
            if (setMethod == null)
                throw new Exception("setMethod is null");

            var reactiveAttribute = ModuleDefinition.FindType("SexyReact", "RxAttribute", sexyReact);
            if (reactiveAttribute == null)
                throw new Exception("reactiveAttribute is null");

            var typeType = ModuleDefinition.Import(typeof(Type));
            var getPropertyByName = ModuleDefinition.Import(typeType.Resolve().Methods.Single(x => x.Name == "GetProperty" && x.Parameters.Count == 1));
            var getTypeFromTypeHandle = ModuleDefinition.Import(typeType.Resolve().Methods.Single(x => x.Name == "GetTypeFromHandle"));
            foreach (var targetType in targetTypes)
            {
                var rxForClass = targetType.IsDefined(reactiveAttribute, true);
                var logger = rxForClass ? LogInfo : LogWarning;

                PropertyDefinition[] properties;

                // If [Rx] has been applied to a class, then all of its properties are considered Rx
                if (rxForClass)
                {
                    var mostAncestralClassWithRx = targetType.GetEarliestAncestorThatDeclares(reactiveAttribute);
                    properties = targetType.Properties.Where(x => mostAncestralClassWithRx.IsAssignableFrom(x.DeclaringType)).ToArray();
                }
                // Otherwise, only properties decorated with [Rx] are considered
                else
                {
                    properties = targetType.Properties.Where(x => x.IsDefined(reactiveAttribute)).ToArray();
                }

                if (properties.Any())
                {
                    var staticConstructor = targetType.GetStaticConstructor();
                    if (staticConstructor == null)
                    {
//                        LogInfo("Creating static constructor");
                        staticConstructor = new MethodDefinition(".cctor", MethodAttributes.Static | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, ModuleDefinition.TypeSystem.Void);
                        staticConstructor.Body = new MethodBody(staticConstructor);
                        targetType.Methods.Add(staticConstructor);
                    }
                    else
                    {
                        staticConstructor.Body.Instructions.RemoveAt(staticConstructor.Body.Instructions.Count - 1);
                    }
                    foreach (var property in properties)
                    {
                        LogInfo($"{targetType}.{property}");

                        if (property.GetMethod == null || property.SetMethod == null)
                        {
                            logger($"Rx properties must have both a getter and a setter.  Skipping {targetType}.{property.Name}");
                            continue;
                        }

                        // Remove old field (the generated backing field for the auto property)
                        var oldFieldInstruction = property.GetMethod.Body.Instructions.Where(x => x.Operand is FieldReference).SingleOrDefault();
                        if (oldFieldInstruction == null)
                        {
                            logger($"Rx properties must be auto-properties.  The backing field for property {targetType}.{property.Name} not found.");
                            continue;
                        }
                        var oldField = (FieldReference)property.GetMethod.Body.Instructions.Where(x => x.Operand is FieldReference).Single().Operand;
                        var oldFieldDefinition = oldField.Resolve();
                        targetType.Fields.Remove(oldFieldDefinition);

                        // See if there exists an initializer for the auto-property
                        var constructors = targetType.Methods.Where(x => x.IsConstructor);                    foreach (var constructor in constructors)
                        {
                            var fieldAssignment = constructor.Body.Instructions.SingleOrDefault(x => Equals(x.Operand, oldFieldDefinition) || Equals(x.Operand, oldField));
                            if (fieldAssignment != null)
                            {
                                // Replace field assignment with a property set (the stack semantics are the same for both, 
                                // so happily we don't have to manipulate the bytecode any further.)
                                var setterCall = constructor.Body.GetILProcessor().Create(property.SetMethod.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, property.SetMethod);
                                constructor.Body.GetILProcessor().Replace(fieldAssignment, setterCall);
                            }
                        }

                        // Add a static field for the property's property info
                        var propertyInfoField = new FieldDefinition(property.Name + "$PropertyInfo", FieldAttributes.Private | FieldAttributes.Static, propertyInfoType);
                        targetType.Fields.Add(propertyInfoField);

                        staticConstructor.Body.Emit(il =>
                        {
                            il.Emit(OpCodes.Ldtoken, targetType);
                            il.Emit(OpCodes.Call, getTypeFromTypeHandle);
                            il.Emit(OpCodes.Ldstr, property.Name);
                            il.Emit(OpCodes.Call, getPropertyByName);
                            il.Emit(OpCodes.Stsfld, propertyInfoField);
                        });

                        // Build out the getter which returns Get<TValue>(property.Name$PropertyInfo);
                        property.GetMethod.Body = new MethodBody(property.GetMethod);
                        var getMethodReference = getMethod.MakeGenericMethod(property.PropertyType);
                        property.GetMethod.Body.Emit(il =>
                        {
                            il.Emit(OpCodes.Ldarg_0);                                   // this
                            il.Emit(OpCodes.Ldsfld, propertyInfoField);                 // property.Name$PropertyInfo
                            il.Emit(OpCodes.Callvirt, getMethodReference);              // pop * 2 -> this.Get(property.Name$PropertyName)
                            il.Emit(OpCodes.Ret);                                       // Return the field value that is lying on the stack
                        });
                    
                        var setMethodReference = setMethod.MakeGenericMethod(property.PropertyType);

                        // Build out the setter which fires the RaiseAndSetIfChanged method
                        property.SetMethod.Body = new MethodBody(property.SetMethod);
                        property.SetMethod.Body.Emit(il =>
                        {
                            il.Emit(OpCodes.Ldarg_0);                                   // this
                            il.Emit(OpCodes.Ldsfld, propertyInfoField);                 // property.Name$PropertyInfo
                            il.Emit(OpCodes.Ldarg_1);                                   // value
                            il.Emit(OpCodes.Callvirt, setMethodReference);              // pop * 2 -> this.Get(property.Name$PropertyName)
                            il.Emit(OpCodes.Ret);                                       // return out of the method
                        });
                    }                    
                    staticConstructor.Body.Emit(il =>
                    {
                        il.Emit(OpCodes.Ret);
                    });
                }
            }
        }         
    }
}