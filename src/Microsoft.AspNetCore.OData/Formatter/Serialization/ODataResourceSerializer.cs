//-----------------------------------------------------------------------------
// <copyright file="ODataResourceSerializer.cs" company=".NET Foundation">
//      Copyright (c) .NET Foundation and Contributors. All rights reserved.
//      See License.txt in the project root for license information.
// </copyright>
//------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Microsoft.AspNetCore.OData.Edm;
using Microsoft.AspNetCore.OData.Extensions;
using Microsoft.AspNetCore.OData.Formatter.Value;
using Microsoft.AspNetCore.OData.Query.Wrapper;
using Microsoft.AspNetCore.OData.Routing;
using Microsoft.OData;
using Microsoft.OData.ModelBuilder;
using Microsoft.OData.Edm;
using Microsoft.OData.UriParser;
using NavigationSourceLinkBuilderAnnotation = Microsoft.AspNetCore.OData.Edm.NavigationSourceLinkBuilderAnnotation;
using Microsoft.AspNetCore.OData.Common;
using System.Threading.Tasks;
using Microsoft.AspNetCore.OData.Deltas;

namespace Microsoft.AspNetCore.OData.Formatter.Serialization;

/// <summary>
/// ODataSerializer for serializing instances of <see cref="IEdmEntityType"/> or <see cref="IEdmComplexType"/>
/// </summary>
[SuppressMessage("Microsoft.Maintainability", "CA1506:AvoidExcessiveClassCoupling", Justification = "Relies on many ODataLib classes.")]
public class ODataResourceSerializer : ODataEdmTypeSerializer
{
    private const string Resource = "Resource";

    /// <inheritdoc />
    public ODataResourceSerializer(IODataSerializerProvider serializerProvider)
        : base(ODataPayloadKind.Resource, serializerProvider)
    {
    }

    /// <inheritdoc />
    public override async Task WriteObjectAsync(object graph, Type type, ODataMessageWriter messageWriter,
        ODataSerializerContext writeContext)
    {
        if (messageWriter == null)
        {
            throw Error.ArgumentNull(nameof(messageWriter));
        }

        if (writeContext == null)
        {
            throw Error.ArgumentNull(nameof(writeContext));
        }

        bool isUntypedPath = writeContext.Path.IsUntypedPropertyPath();
        IEdmTypeReference edmType = writeContext.GetEdmType(graph, type, isUntypedPath);
        Contract.Assert(edmType != null);

        IEdmNavigationSource navigationSource = writeContext.NavigationSource;
        ODataWriter writer = await messageWriter.CreateODataResourceWriterAsync(navigationSource, edmType.ToStructuredType())
            .ConfigureAwait(false);
        await WriteObjectInlineAsync(graph, edmType, writer, writeContext).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task WriteObjectInlineAsync(object graph, IEdmTypeReference expectedType, ODataWriter writer,
        ODataSerializerContext writeContext)
    {
        if (writer == null)
        {
            throw Error.ArgumentNull(nameof(writer));
        }

        if (writeContext == null)
        {
            throw Error.ArgumentNull(nameof(writeContext));
        }

        if (graph == null || graph is NullEdmComplexObject)
        {
            throw new SerializationException(Error.Format(SRResources.CannotSerializerNull, Resource));
        }
        else
        {
            await WriteResourceAsync(graph, writer, writeContext, expectedType).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes the given object specified by the parameter graph as a part of an existing OData message using the given
    /// deltaWriter and the writeContext.
    /// </summary>
    /// <param name="graph">The object to be written.</param>
    /// <param name="expectedType">The expected EDM type of the object represented by <paramref name="graph"/>.</param>
    /// <param name="writer">The <see cref="ODataDeltaWriter" /> to be used for writing.</param>
    /// <param name="writeContext">The <see cref="ODataSerializerContext"/>.</param>
    public virtual async Task WriteDeltaObjectInlineAsync(object graph, IEdmTypeReference expectedType, ODataWriter writer,
       ODataSerializerContext writeContext)
    {
        if (writer == null)
        {
            throw Error.ArgumentNull(nameof(writer));
        }

        if (writeContext == null)
        {
            throw Error.ArgumentNull(nameof(writeContext));
        }

        if (graph == null)
        {
            throw new SerializationException(Error.Format(SRResources.CannotSerializerNull, Resource));
        }
        else
        {
            await WriteResourceAsync(graph, writer, writeContext, expectedType).ConfigureAwait(false);
        }
    }

    private async Task WriteDeltaComplexPropertiesAsync(SelectExpandNode selectExpandNode,
        ResourceContext resourceContext, ODataWriter writer)
    {
        Contract.Assert(resourceContext != null);
        Contract.Assert(writer != null);

        if (selectExpandNode.SelectedComplexProperties == null)
        {
            return;
        }

        IEnumerable<IEdmStructuralProperty> complexProperties = selectExpandNode.SelectedComplexProperties.Keys;

        if (resourceContext.EdmObject != null && resourceContext.SerializerContext.IsDelta)
        {
            IDelta deltaObject = null;
            if (resourceContext.EdmObject is TypedEdmEntityObject obj)
            {
                deltaObject = obj.Instance as IDelta;
            }
            else
            {
                deltaObject = resourceContext.EdmObject as IDelta;
            }

            if (deltaObject != null)
            {
                IEnumerable<string> changedProperties = deltaObject.GetChangedPropertyNames();
                complexProperties = complexProperties.Where(p => changedProperties.Contains(p.Name));
            }
        }

        foreach (IEdmStructuralProperty complexProperty in complexProperties)
        {
            PathSelectItem selectItem = selectExpandNode.SelectedComplexProperties[complexProperty];
            ODataNestedResourceInfo nestedResourceInfo = CreateComplexNestedResourceInfo(complexProperty, selectExpandNode.SelectedComplexProperties[complexProperty], resourceContext);

            await writer.WriteStartAsync(nestedResourceInfo).ConfigureAwait(false);
            await WriteDeltaComplexAndExpandedNavigationPropertyAsync(complexProperty, selectItem, resourceContext, writer)
                .ConfigureAwait(false);
            await writer.WriteEndAsync().ConfigureAwait(false);
        }
    }

    private async Task WriteDeltaComplexAndExpandedNavigationPropertyAsync(
        IEdmProperty edmProperty,
        SelectItem selectItem,
        ResourceContext resourceContext,
        ODataWriter writer,
        Type navigationPropertyType = null)
    {
        Contract.Assert(edmProperty != null);
        Contract.Assert(resourceContext != null);
        Contract.Assert(writer != null);

        object propertyValue = resourceContext.GetPropertyValue(edmProperty.Name);

        if (propertyValue == null || propertyValue is NullEdmComplexObject)
        {
            if (edmProperty.Type.IsCollection())
            {
                // A complex or navigation property whose Type attribute specifies a collection, the collection always exists,
                // it may just be empty.
                // If a collection of complex or entities can be related, it is represented as a JSON array. An empty
                // collection of resources (one that contains no resource) is represented as an empty JSON array.
                await writer.WriteStartAsync(new ODataResourceSet
                {
                    TypeName = edmProperty.Type.FullName()
                }).ConfigureAwait(false);
            }
            else
            {
                // If at most one resource can be related, the value is null if no resource is currently related.
                await writer.WriteStartAsync(resource: null).ConfigureAwait(false);
            }

            await writer.WriteEndAsync().ConfigureAwait(false);
        }
        else
        {
            // create the serializer context for the complex and expanded item.
            ODataSerializerContext nestedWriteContext = new ODataSerializerContext(resourceContext, edmProperty, resourceContext.SerializerContext.QueryContext, selectItem);
            nestedWriteContext.Type = navigationPropertyType;

            // write object.
            if (edmProperty.Type.IsCollection())
            {
                if (IsDeltaCollection(propertyValue))
                {
                    // TODO: enable overriding serializer based on type. Currently requires serializer supports WriteDeltaObjectinline, because it takes an ODataDeltaWriter
                    // ODataEdmTypeSerializer serializer = SerializerProvider.GetEdmTypeSerializer(edmProperty.Type);
                    // if (serializer == null)
                    // {
                    //     throw new SerializationException(
                    //         Error.Format(SRResources.TypeCannotBeSerialized, edmProperty.Type.ToTraceString()));
                    // }
                    ODataEdmTypeSerializer serializer = new ODataDeltaResourceSetSerializer(SerializerProvider);
                    IEdmEntityType itemType = edmProperty.Type.GetElementType() as IEdmEntityType;
                    if(itemType == null)
                    {
                        throw new SerializationException(
                            Error.Format(SRResources.TypeCannotBeSerialized, edmProperty.Type.ToTraceString()));
                    }

                    await serializer.WriteObjectInlineAsync(
                        propertyValue,
                        edmProperty.Type,
                        writer,
                        nestedWriteContext).ConfigureAwait(false);
                }
                else
                {
                    IODataEdmTypeSerializer serializer = SerializerProvider.GetEdmTypeSerializer(edmProperty.Type);
                    if (serializer == null)
                    {
                        throw new SerializationException(
                            Error.Format(SRResources.TypeCannotBeSerialized, edmProperty.Type.ToTraceString()));
                    }

                    await serializer.WriteObjectInlineAsync(propertyValue, edmProperty.Type, writer, nestedWriteContext).ConfigureAwait(false);
                }
            }
            else
            {
                await WriteDeltaObjectInlineAsync(propertyValue, edmProperty.Type, writer, nestedWriteContext).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Writes delta navigation properties asynchronously.
    /// </summary>
    /// <param name="selectExpandNode">Contains the set of properties and actions to use to select and expand while writing an entity.</param>
    /// <param name="resourceContext">The resource context for the resource being written.</param>
    /// <param name="writer">The ODataWriter.</param>
    /// <returns>A task that represents the asynchronous write operation</returns>
    private async Task WriteDeltaNavigationPropertiesAsync(SelectExpandNode selectExpandNode, ResourceContext resourceContext, ODataWriter writer)
    {
        Contract.Assert(resourceContext != null, "The ResourceContext cannot be null");
        Contract.Assert(writer != null, "The ODataWriter cannot be null");

        IEnumerable<KeyValuePair<IEdmNavigationProperty, Type>> navigationProperties = GetNavigationPropertiesToWrite(selectExpandNode, resourceContext);

        foreach (KeyValuePair<IEdmNavigationProperty, Type> navigationProperty in navigationProperties)
        {
            ODataNestedResourceInfo nestedResourceInfo = CreateNavigationLink(navigationProperty.Key, resourceContext);
            await writer.WriteStartAsync(nestedResourceInfo).ConfigureAwait(false);
            await WriteDeltaComplexAndExpandedNavigationPropertyAsync(navigationProperty.Key, null, resourceContext, writer, navigationProperty.Value).ConfigureAwait(false);
            await writer.WriteEndAsync().ConfigureAwait(false);
        }
    }

    private IEnumerable<KeyValuePair<IEdmNavigationProperty, Type>> GetNavigationPropertiesToWrite(SelectExpandNode selectExpandNode, ResourceContext resourceContext)
    {
        IEnumerable<IEdmNavigationProperty> navigationProperties = selectExpandNode.ExpandedProperties?.Keys;

        if (navigationProperties == null)
        {
            navigationProperties = resourceContext.StructuredType.DeclaredNavigationProperties();
        }

        if (resourceContext.EdmObject is IDelta changedObject)
        {
            IEnumerable<string> changedProperties = changedObject.GetChangedPropertyNames();

            foreach (IEdmNavigationProperty navigationProperty in navigationProperties)
            {
                if (changedProperties != null && changedProperties.Contains(navigationProperty.Name))
                {
                    yield return new KeyValuePair<IEdmNavigationProperty, Type>(navigationProperty, typeof(IEdmChangedObject));
                }
            }
        }
        else if (resourceContext.ResourceInstance is IDelta deltaObject)
        {
            IEnumerable<string> changedProperties = deltaObject.GetChangedPropertyNames();
            IDictionary<string, object> deltaNestedProperties = deltaObject.GetDeltaNestedNavigationProperties();

            foreach (IEdmNavigationProperty navigationProperty in navigationProperties)
            {
                if (changedProperties != null && changedProperties.Contains(navigationProperty.Name) && deltaNestedProperties.TryGetValue(navigationProperty.Name, out object obj))
                {
                    if (obj != null)
                    {
                        yield return new KeyValuePair<IEdmNavigationProperty, Type>(navigationProperty, obj.GetType());
                    }
                }
            }
        }
    }

    private static IEnumerable<ODataProperty> CreateODataPropertiesFromDynamicType(EdmStructuredType structuredType, object graph,
        Dictionary<IEdmProperty, object> dynamicTypeProperties, ODataSerializerContext writeContext)
    {
        Contract.Assert(dynamicTypeProperties != null);

        var properties = new List<ODataProperty>();
        var dynamicObject = graph as DynamicTypeWrapper;
        if (dynamicObject == null)
        {
            var dynamicEnumerable = (graph as IEnumerable<DynamicTypeWrapper>);
            if (dynamicEnumerable != null)
            {
                dynamicObject = dynamicEnumerable.SingleOrDefault();
            }
        }
        if (dynamicObject != null)
        {
            foreach (var prop in dynamicObject.Values)
            {
                IEdmProperty edmProperty = structuredType?.Properties()
                        .FirstOrDefault(p => p.Name.Equals(prop.Key, StringComparison.Ordinal));

                if (prop.Value != null
                    && (prop.Value is DynamicTypeWrapper || (prop.Value is IEnumerable<DynamicTypeWrapper>)))
                {
                    if (edmProperty != null)
                    {
                        dynamicTypeProperties.Add(edmProperty, prop.Value);
                    }
                }
                else
                {
                    ODataProperty property;
                    if (prop.Value == null)
                    {
                        property = new ODataProperty
                        {
                            Name = prop.Key,
                            Value = ODataNullValueExtensions.NullValue
                        };
                    }
                    else
                    {
                        if (edmProperty != null)
                        {
                            property = new ODataProperty
                            {
                                Name = prop.Key,
                                Value = ODataPrimitiveSerializer.ConvertPrimitiveValue(prop.Value, edmProperty.Type.AsPrimitive(), writeContext?.TimeZone)
                            };
                        }
                        else
                        {
                            property = new ODataProperty
                            {
                                Name = prop.Key,
                                Value = prop.Value
                            };
                        }
                    }

                    properties.Add(property);
                }
            }
        }

        return properties;
    }

    private async Task WriteDynamicTypeResourceAsync(object graph, ODataWriter writer, IEdmTypeReference expectedType,
        ODataSerializerContext writeContext)
    {
        var dynamicTypeProperties = new Dictionary<IEdmProperty, object>();
        var structuredType = expectedType.Definition as EdmStructuredType;
        var resource = new ODataResource()
        {
            TypeName = expectedType.FullName(),
            Properties = CreateODataPropertiesFromDynamicType(structuredType, graph, dynamicTypeProperties, writeContext)
        };

        resource.IsTransient = true;
        await writer.WriteStartAsync(resource).ConfigureAwait(false);
        foreach (var property in dynamicTypeProperties.Keys)
        {
            var resourceContext = new ResourceContext(writeContext, expectedType.AsStructured(), graph);
            if (structuredType.NavigationProperties().Any(p => p.Type.Equals(property.Type)) && !(property.Type is EdmCollectionTypeReference))
            {
                var navigationProperty = structuredType.NavigationProperties().FirstOrDefault(p => p.Type.Equals(property.Type));
                var navigationLink = CreateNavigationLink(navigationProperty, resourceContext);
                if (navigationLink != null)
                {
                    await writer.WriteStartAsync(navigationLink).ConfigureAwait(false);
                    await WriteDynamicTypeResourceAsync(dynamicTypeProperties[property], writer, property.Type, writeContext)
                        .ConfigureAwait(false);
                    await writer.WriteEndAsync().ConfigureAwait(false);
                }
            }
            else
            {
                ODataNestedResourceInfo nestedResourceInfo = new ODataNestedResourceInfo
                {
                    IsCollection = property.Type.IsCollection(),
                    Name = property.Name
                };

                await writer.WriteStartAsync(nestedResourceInfo).ConfigureAwait(false);
                await WriteDynamicComplexPropertyAsync(dynamicTypeProperties[property], property.Type, resourceContext, writer)
                    .ConfigureAwait(false);
                await writer.WriteEndAsync().ConfigureAwait(false);
            }
        }

        await writer.WriteEndAsync().ConfigureAwait(false);
    }

    private async Task WriteResourceAsync(object graph, ODataWriter writer, ODataSerializerContext writeContext,
        IEdmTypeReference expectedType)
    {
        Contract.Assert(writeContext != null);

        if (graph.GetType().IsDynamicTypeWrapper())
        {
            await WriteDynamicTypeResourceAsync(graph, writer, expectedType, writeContext).ConfigureAwait(false);
            return;
        }

        IEdmStructuredTypeReference structuredType = GetResourceType(graph, writeContext);
        ResourceContext resourceContext = new ResourceContext(writeContext, structuredType, graph);

        IEdmNavigationSource originalNavigationSource = writeContext.NavigationSource;
        if (graph is EdmDeltaResourceObject deltaResource && deltaResource?.NavigationSource != null)
        {
            resourceContext.NavigationSource = deltaResource.NavigationSource;
        }

        SelectExpandNode selectExpandNode = CreateSelectExpandNode(resourceContext);
        if (selectExpandNode != null)
        {
            if (graph is IDeltaDeletedResource || graph is IEdmDeltaDeletedResourceObject)
            {
                ODataDeletedResource odataDeletedResource;

                if (graph is EdmDeltaDeletedResourceObject edmDeltaDeletedEntity)
                {
                    odataDeletedResource = CreateDeletedResource(edmDeltaDeletedEntity.Id, edmDeltaDeletedEntity.Reason ?? DeltaDeletedEntryReason.Deleted, selectExpandNode, resourceContext);
                    if (edmDeltaDeletedEntity.NavigationSource != null)
                    {
                        resourceContext.NavigationSource = edmDeltaDeletedEntity.NavigationSource;
                        ODataResourceSerializationInfo serializationInfo = new ODataResourceSerializationInfo
                        {
                            NavigationSourceName = edmDeltaDeletedEntity.NavigationSource.Name
                        };
                        odataDeletedResource.SetSerializationInfo(serializationInfo);
                    }
                }
                else if (graph is IDeltaDeletedResource deltaDeletedResource)
                {
                    odataDeletedResource = CreateDeletedResource(deltaDeletedResource.Id, deltaDeletedResource.Reason ?? DeltaDeletedEntryReason.Deleted, selectExpandNode, resourceContext);
                }
                else
                {
                    throw new SerializationException(Error.Format(SRResources.CannotWriteType, GetType().Name, graph?.GetType().FullName));
                }

                await writer.WriteStartAsync(odataDeletedResource).ConfigureAwait(false);
                await WriteResourceContent(writer, selectExpandNode, resourceContext).ConfigureAwait(false);
                await writer.WriteEndAsync().ConfigureAwait(false);
            }
            else
            {
                ODataResource resource = CreateResource(selectExpandNode, resourceContext);
                if (resource != null)
                {
                    if (resourceContext.SerializerContext.ExpandReference)
                    {
                        await writer.WriteEntityReferenceLinkAsync(new ODataEntityReferenceLink
                        {
                            Url = resource.Id
                        }).ConfigureAwait(false);
                    }
                    else
                    {
                        await writer.WriteStartAsync(resource).ConfigureAwait(false);
                        await WriteResourceContent(writer, selectExpandNode, resourceContext).ConfigureAwait(false);
                        await writer.WriteEndAsync().ConfigureAwait(false);
                    }
                }
            }

            writeContext.NavigationSource = originalNavigationSource;
        }
    }

    /// <summary>
    /// Writes the context of a Resource
    /// </summary>
    /// <param name="writer">The <see cref="ODataWriter" /> to use to write the resource contents</param>
    /// <param name="selectExpandNode">The <see cref="SelectExpandNode"/> describing the response graph.</param>
    /// <param name="resourceContext">The context for the resource instance being written.</param>
    private async Task WriteResourceContent(ODataWriter writer, SelectExpandNode selectExpandNode, ResourceContext resourceContext)
    {
        // TODO: These should be aligned; do we need different methods for delta versus non-delta complex/navigation properties?
        if (resourceContext.SerializerContext.IsDelta)
        {
            await WriteUntypedPropertiesAsync(selectExpandNode, resourceContext, writer).ConfigureAwait(false);
            await WriteStreamPropertiesAsync(selectExpandNode, resourceContext, writer).ConfigureAwait(false);
            await WriteDeltaComplexPropertiesAsync(selectExpandNode, resourceContext, writer).ConfigureAwait(false);
            //await WriteComplexPropertiesAsync(selectExpandNode, resourceContext, writer).ConfigureAwait(false);
            await WriteDynamicComplexPropertiesAsync(resourceContext, writer).ConfigureAwait(false);
            await WriteDeltaNavigationPropertiesAsync(selectExpandNode, resourceContext, writer).ConfigureAwait(false);
            //await WriteExpandedNavigationPropertiesAsync(selectExpandNode, resourceContext, writer).ConfigureAwait(false);
        }
        else
        {
            await WriteUntypedPropertiesAsync(selectExpandNode, resourceContext, writer).ConfigureAwait(false);
            await WriteStreamPropertiesAsync(selectExpandNode, resourceContext, writer).ConfigureAwait(false);
            await WriteComplexPropertiesAsync(selectExpandNode, resourceContext, writer).ConfigureAwait(false);
            await WriteDynamicComplexPropertiesAsync(resourceContext, writer).ConfigureAwait(false);
            await WriteNavigationLinksAsync(selectExpandNode, resourceContext, writer).ConfigureAwait(false);
            await WriteExpandedNavigationPropertiesAsync(selectExpandNode, resourceContext, writer).ConfigureAwait(false);
            await WriteReferencedNavigationPropertiesAsync(selectExpandNode, resourceContext, writer).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Creates the <see cref="SelectExpandNode"/> that describes the set of properties and actions to select and expand while writing this entity.
    /// </summary>
    /// <param name="resourceContext">Contains the entity instance being written and the context.</param>
    /// <returns>
    /// The <see cref="SelectExpandNode"/> that describes the set of properties and actions to select and expand while writing this entity.
    /// </returns>
    public virtual SelectExpandNode CreateSelectExpandNode(ResourceContext resourceContext)
    {
        if (resourceContext == null)
        {
            throw Error.ArgumentNull(nameof(resourceContext));
        }

        ODataSerializerContext writeContext = resourceContext.SerializerContext;
        IEdmStructuredType structuredType = resourceContext.StructuredType;

        object selectExpandNode;

        Tuple<SelectExpandClause, IEdmStructuredType> key = Tuple.Create(writeContext.SelectExpandClause, structuredType);
        if (!writeContext.Items.TryGetValue(key, out selectExpandNode))
        {
            // cache the selectExpandNode so that if we are writing a feed we don't have to construct it again.
            selectExpandNode = new SelectExpandNode(structuredType, writeContext);
            writeContext.Items[key] = selectExpandNode;
        }

        return selectExpandNode as SelectExpandNode;
    }

    /// <summary>
    /// Creates the <see cref="ODataResource"/> to be written while writing this resource.
    /// </summary>
    /// <param name="selectExpandNode">The <see cref="SelectExpandNode"/> describing the response graph.</param>
    /// <param name="resourceContext">The context for the resource instance being written.</param>
    /// <returns>The created <see cref="ODataResource"/>.</returns>
    public virtual ODataResource CreateResource(SelectExpandNode selectExpandNode, ResourceContext resourceContext)
    {
        if (selectExpandNode == null)
        {
            throw Error.ArgumentNull(nameof(selectExpandNode));
        }

        if (resourceContext == null)
        {
            throw Error.ArgumentNull(nameof(resourceContext));
        }

        if (resourceContext.SerializerContext.ExpandReference)
        {
            return new ODataResource
            {
                Id = resourceContext.GenerateSelfLink(false)
            };
        }

        string typeName = resourceContext.StructuredType.FullTypeName();

        ODataResource resource = new ODataResource
        {
            TypeName = typeName ?? "Edm.Untyped",
            Properties = CreateStructuralPropertyBag(selectExpandNode, resourceContext),
        };

        InitializeODataResource(selectExpandNode, resource, resourceContext);

        // Try to add the dynamic properties if the structural type is open.
        AppendDynamicProperties(resource, selectExpandNode, resourceContext);

        if (selectExpandNode.SelectedActions != null)
        {
            IEnumerable<ODataAction> actions = CreateODataActions(selectExpandNode.SelectedActions, resourceContext);
            foreach (ODataAction action in actions)
            {
                resource.AddAction(action);
            }
        }

        if (selectExpandNode.SelectedFunctions != null)
        {
            IEnumerable<ODataFunction> functions = CreateODataFunctions(selectExpandNode.SelectedFunctions, resourceContext);
            foreach (ODataFunction function in functions)
            {
                resource.AddFunction(function);
            }
        }

        return resource;
    }

    /// <summary>
    /// Creates the <see cref="ODataResource"/> to be written while writing this resource.
    /// </summary>
    /// <param name="id">The id of the Deleted Resource to be written (may be null if properties contains all key properties)</param>
    /// <param name="reason">The <see cref="DeltaDeletedEntryReason"/> for the removal of the resource.</param>
    /// <param name="selectExpandNode">The <see cref="SelectExpandNode"/> describing the response graph.</param>
    /// <param name="resourceContext">The context for the resource instance being written.</param>
    /// <returns>The created <see cref="ODataResource"/>.</returns>
    public virtual ODataDeletedResource CreateDeletedResource(Uri id, DeltaDeletedEntryReason reason, SelectExpandNode selectExpandNode, ResourceContext resourceContext)
    {
        if (selectExpandNode == null)
        {
            throw Error.ArgumentNull(nameof(selectExpandNode));
        }

        if (resourceContext == null)
        {
            throw Error.ArgumentNull(nameof(resourceContext));
        }

        string typeName = resourceContext.StructuredType.FullTypeName();

        ODataDeletedResource resource = new ODataDeletedResource
        {
            Id = id ?? (resourceContext.NavigationSource == null ? null : resourceContext.GenerateSelfLink(false)),
            TypeName = typeName ?? "Edm.Untyped",
            Properties = CreateStructuralPropertyBag(selectExpandNode, resourceContext),
            Reason = reason
        };

        InitializeODataResource(selectExpandNode, resource, resourceContext);

        // Try to add the dynamic properties if the structural type is open.
        AppendDynamicPropertiesInternal(resource, selectExpandNode, resourceContext);

        return resource;
    }

    /// <summary>
    /// Initializes an ODataResource to be written
    /// </summary>
    /// <param name="selectExpandNode">The <see cref="SelectExpandNode"/> describing the response graph.</param>
    /// <param name="resource">The resource that will be initialized</param>
    /// <param name="resourceContext">The context for the resource instance being written.</param>
    private void InitializeODataResource(SelectExpandNode selectExpandNode, ODataResourceBase resource, ResourceContext resourceContext)
    { 
        if ((resourceContext.EdmObject is EdmDeltaResourceObject || resourceContext.EdmObject is IEdmDeltaDeletedResourceObject) && resourceContext.NavigationSource != null)
        {
            ODataResourceSerializationInfo serializationInfo = new ODataResourceSerializationInfo();
            serializationInfo.NavigationSourceName = resourceContext.NavigationSource.Name;
            serializationInfo.NavigationSourceKind = resourceContext.NavigationSource.NavigationSourceKind();
            IEdmEntityType sourceType = resourceContext.NavigationSource.EntityType;
            if (sourceType != null)
            {
                serializationInfo.NavigationSourceEntityTypeName = sourceType.Name;
            }
            resource.SetSerializationInfo(serializationInfo);
        }

        IEdmStructuredType pathType = GetODataPathType(resourceContext.SerializerContext);
        if (resourceContext.StructuredType.TypeKind == EdmTypeKind.Complex)
        {
            AddTypeNameAnnotationAsNeededForComplex(resource, resourceContext.SerializerContext.MetadataLevel);
        }
        else
        {
            AddTypeNameAnnotationAsNeeded(resource, pathType, resourceContext.SerializerContext.MetadataLevel);
        }

        if (resourceContext.StructuredType.TypeKind == EdmTypeKind.Entity && resourceContext.NavigationSource != null)
        {
            // Condition 1. If resourceContext.NavigationSource is a contained entity set
            //    and a contained resource is being written, the id/read/edit links can be derived
            //    from the entity set or parent resource, i.e., no need to use link builder to build the links.
            // Condition 2. If resourceContext.NavigationSource is a contained entity set
            //    but an expanded non-contained resource is being written,
            //    deriving the id/read/edit links from the entity set or parent resource will
            //    most likely result into invalid links.
            //    A navigation property binding should exist and we should try
            //    to use the navigation link builder to build the links.
            // NOTE: resourceContext.SerializerContext.NavigationProperty will not be null when writing an expanded resource
            if (!(resourceContext.NavigationSource is IEdmContainedEntitySet)
                || resourceContext.SerializerContext.NavigationProperty?.ContainsTarget == false)
            {
                IEdmModel model = resourceContext.SerializerContext.Model;
                NavigationSourceLinkBuilderAnnotation linkBuilder = EdmModelLinkBuilderExtensions.GetNavigationSourceLinkBuilder(model, resourceContext.NavigationSource);
                EntitySelfLinks selfLinks = linkBuilder.BuildEntitySelfLinks(resourceContext, resourceContext.SerializerContext.MetadataLevel);

                if (resource.Id == null && selfLinks.IdLink != null)
                {
                    resource.Id = selfLinks.IdLink;
                }

                if (resource.ReadLink == null && selfLinks.ReadLink != null)
                {
                    resource.ReadLink = selfLinks.ReadLink;
                }

                if (resource.EditLink == null && selfLinks.EditLink != null)
                {
                    resource.EditLink = selfLinks.EditLink;
                }
            }

            string etag = CreateETag(resourceContext);
            if (etag != null)
            {
                resource.ETag = etag;
            }
        }
   }

    /// <summary>
    /// Appends the dynamic properties of primitive, enum or the collection of them into the given <see cref="ODataResource"/>.
    /// If the dynamic property is a property of the complex or collection of complex, it will be saved into
    /// the dynamic complex properties dictionary of <paramref name="resourceContext"/> and be written later.
    /// </summary>
    /// <param name="resource">The <see cref="ODataResource"/> describing the resource.</param>
    /// <param name="selectExpandNode">The <see cref="SelectExpandNode"/> describing the response graph.</param>
    /// <param name="resourceContext">The context for the resource instance being written.</param>
    [SuppressMessage("Microsoft.Maintainability", "CA1506:AvoidExcessiveClassCoupling", Justification = "Relies on many classes.")]
    [SuppressMessage("Microsoft.Maintainability", "CA1502:AvoidExcessiveComplexity", Justification = "These are simple conversion function and cannot be split up.")]
    public virtual void AppendDynamicProperties(ODataResource resource, SelectExpandNode selectExpandNode,
        ResourceContext resourceContext)
    {
        AppendDynamicPropertiesInternal(resource, selectExpandNode, resourceContext);
    }

    /// <summary>
    /// Appends the dynamic properties of primitive, enum or the collection of them into the given <see cref="ODataResource"/>.
    /// If the dynamic property is a property of the complex or collection of complex, it will be saved into
    /// the dynamic complex properties dictionary of <paramref name="resourceContext"/> and be written later.
    /// </summary>
    /// <param name="resource">The <see cref="ODataResource"/> describing the resource.</param>
    /// <param name="selectExpandNode">The <see cref="SelectExpandNode"/> describing the response graph.</param>
    /// <param name="resourceContext">The context for the resource instance being written.</param>
    private void AppendDynamicPropertiesInternal(ODataResourceBase resource, SelectExpandNode selectExpandNode,
        ResourceContext resourceContext)
    {
        Contract.Assert(resource != null);
        Contract.Assert(selectExpandNode != null);
        Contract.Assert(resourceContext != null);

        if (!resourceContext.StructuredType.IsOpen || // non-open type
    (!selectExpandNode.SelectAllDynamicProperties && selectExpandNode.SelectedDynamicProperties == null))
        {
            return;
        }

        IEdmStructuredType structuredType = resourceContext.StructuredType;
        IEdmStructuredObject structuredObject = resourceContext.EdmObject;
        ODataSerializerContext serializierContext = resourceContext.SerializerContext;
        object value;
        IDelta delta = structuredObject as IDelta;
        if (structuredObject is EdmUntypedObject untypedObject) // NO CLR, NO EDM
        {
            value = untypedObject;
        }
        else if (structuredObject is TypedEdmUntypedObject typedUntypedObject) // has CLR, but no EDM
        {
            value = typedUntypedObject.GetProperties();
        }
        else if (delta == null)
        {
            PropertyInfo dynamicPropertyInfo = resourceContext.EdmModel.GetDynamicPropertyDictionary(structuredType);
            if (dynamicPropertyInfo == null || structuredObject == null ||
                !structuredObject.TryGetPropertyValue(dynamicPropertyInfo.Name, out value) || value == null)
            {
                return;
            }
        }
        else
        {
            value = ((EdmStructuredObject)structuredObject).TryGetDynamicProperties();
        }

        IDictionary<string, object> dynamicPropertyDictionary = (IDictionary<string, object>)value;

        // Build a HashSet to store the declared property names.
        // It is used to make sure the dynamic property name is different from all declared property names.
        HashSet<string> declaredPropertyNameSet = new HashSet<string>(resource.Properties.Select(p => p.Name));
        List<ODataProperty> dynamicProperties = new List<ODataProperty>();

        // To test SelectedDynamicProperties == null is enough to filter the dynamic properties.
        // Because if SelectAllDynamicProperties == true, SelectedDynamicProperties should be null always.
        // So `selectExpandNode.SelectedDynamicProperties == null` covers `SelectAllDynamicProperties == true` scenario.
        // If `selectExpandNode.SelectedDynamicProperties != null`, then we should test whether the property is selected or not using "Contains(...)".
        IEnumerable<KeyValuePair<string, object>> dynamicPropertiesToSelect =
            dynamicPropertyDictionary.Where(x => selectExpandNode.SelectedDynamicProperties == null || selectExpandNode.SelectedDynamicProperties.Contains(x.Key));
        foreach (KeyValuePair<string, object> dynamicProperty in dynamicPropertiesToSelect)
        {
            if (string.IsNullOrEmpty(dynamicProperty.Key))
            {
                continue;
            }

            if (declaredPropertyNameSet.Contains(dynamicProperty.Key))
            {
                throw Error.InvalidOperation(SRResources.DynamicPropertyNameAlreadyUsedAsDeclaredPropertyName,
                    dynamicProperty.Key, structuredType.FullTypeName());
            }

            object dynamicPropertyValue = dynamicProperty.Value;
            if (dynamicPropertyValue == null)
            {
                dynamicProperties.Add(new ODataProperty
                {
                    Name = dynamicProperty.Key,
                    Value = ODataNullValueExtensions.NullValue
                });

                continue;
            }

            Type propertyType = dynamicPropertyValue.GetType();
            IEdmTypeReference edmTypeReference = serializierContext.GetEdmType(dynamicPropertyValue, propertyType, true);
            if (edmTypeReference == null || edmTypeReference.IsStructuredOrUntyped())
            {
                if (TypeHelper.IsEnum(propertyType))
                {
                    // we don't have the Edm enum type in the model, let's write it as string.
                    dynamicProperties.Add(new ODataProperty
                    {
                        Name = dynamicProperty.Key,

                        // TBD: Shall we write the un-declared enum value as full-name string?
                        // So, "Data":"Apple"  => should be ""Data":"Namespace.EnumTypeName.Apple" ?
                        Value = dynamicPropertyValue.ToString()
                    });

                    continue;
                }

                resourceContext.AppendDynamicOrUntypedProperty(dynamicProperty.Key, dynamicPropertyValue);
            }
            else
            {
                IODataEdmTypeSerializer propertySerializer =SerializerProvider.GetEdmTypeSerializer(edmTypeReference);
                if (propertySerializer == null)
                {
                    throw Error.NotSupported(SRResources.DynamicPropertyCannotBeSerialized, dynamicProperty.Key,
                        edmTypeReference.FullName());
                }

                dynamicProperties.Add(propertySerializer.CreateProperty(
                    dynamicPropertyValue, edmTypeReference, dynamicProperty.Key, serializierContext));
            }
        }

        if (dynamicProperties.Any())
        {
            resource.Properties = resource.Properties.Concat(dynamicProperties);
        }
    }

    /// <summary>
    /// Creates the ETag for the given entity.
    /// </summary>
    /// <param name="resourceContext">The context for the resource instance being written.</param>
    /// <returns>The created ETag.</returns>
    public virtual string CreateETag(ResourceContext resourceContext)
    {
        if (resourceContext == null)
        {
            throw Error.ArgumentNull(nameof(resourceContext));
        }

        if (resourceContext.Request != null)
        {
            IEdmModel model = resourceContext.EdmModel;
            IEdmNavigationSource navigationSource = resourceContext.NavigationSource;

            IEnumerable<IEdmStructuralProperty> concurrencyProperties;
            if (model != null && navigationSource != null)
            {
                concurrencyProperties = model.GetConcurrencyProperties(navigationSource);
            }
            else
            {
                concurrencyProperties = Enumerable.Empty<IEdmStructuralProperty>();
            }

            IDictionary<string, object> properties = null;
            foreach (IEdmStructuralProperty etagProperty in concurrencyProperties)
            {
                properties ??= new SortedDictionary<string, object>();

                properties.Add(etagProperty.Name, resourceContext.GetPropertyValue(etagProperty.Name));
            }

            if (properties != null)
            {
                return resourceContext.Request.CreateETag(properties, resourceContext.TimeZone);
            }
        }

        return null;
    }

    /// <summary>
    /// Write the navigation link for the select navigation properties.
    /// </summary>
    private async Task WriteNavigationLinksAsync(SelectExpandNode selectExpandNode, ResourceContext resourceContext, ODataWriter writer)
    {
        Contract.Assert(selectExpandNode != null);
        Contract.Assert(resourceContext != null);

        if (selectExpandNode.SelectedNavigationProperties == null)
        {
            return;
        }

        IEnumerable<ODataNestedResourceInfo> navigationLinks = CreateNavigationLinks(selectExpandNode.SelectedNavigationProperties, resourceContext);
        foreach (ODataNestedResourceInfo navigationLink in navigationLinks)
        {
            await writer.WriteStartAsync(navigationLink).ConfigureAwait(false);
            await writer.WriteEndAsync().ConfigureAwait(false);
        }
    }

    private async Task WriteDynamicComplexPropertiesAsync(ResourceContext resourceContext, ODataWriter writer)
    {
        Contract.Assert(resourceContext != null);
        Contract.Assert(resourceContext.EdmModel != null);

        if (resourceContext.DynamicComplexProperties == null)
        {
            return;
        }

        foreach (KeyValuePair<string, object> dynamicComplexProperty in resourceContext.DynamicComplexProperties)
        {
            // If the dynamic property is "null", it should be treated ahead by creating an ODataProperty with ODataNullValue.
            // However, it's safety here to skip the null dynamic property.
            if (String.IsNullOrEmpty(dynamicComplexProperty.Key) || dynamicComplexProperty.Value == null)
            {
                continue;
            }

            IEdmTypeReference edmTypeReference =
                resourceContext.SerializerContext.GetEdmType(dynamicComplexProperty.Value,
                    dynamicComplexProperty.Value.GetType(), true);

            if (edmTypeReference.IsStructuredOrUntyped())
            {
                ODataNestedResourceInfo nestedResourceInfo
                    = CreateDynamicComplexNestedResourceInfo(dynamicComplexProperty.Key, dynamicComplexProperty.Value, edmTypeReference, resourceContext);

                if (nestedResourceInfo != null)
                {
                    await writer.WriteStartAsync(nestedResourceInfo).ConfigureAwait(false);
                    await WriteDynamicComplexPropertyAsync(dynamicComplexProperty.Value, edmTypeReference, resourceContext, writer)
                        .ConfigureAwait(false);
                    await writer.WriteEndAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async Task WriteDynamicComplexPropertyAsync(object propertyValue, IEdmTypeReference edmType, ResourceContext resourceContext, ODataWriter writer)
    {
        Contract.Assert(resourceContext != null);
        Contract.Assert(writer != null);

        // If the dynamic property is "null", it should be treated ahead by creating an ODataProperty with ODataNullValue.
        Contract.Assert(propertyValue != null);

        // Create the serializer context for the nested and expanded item.
        ODataSerializerContext nestedWriteContext = new ODataSerializerContext(resourceContext, null, null);

        // Write object.
        IODataEdmTypeSerializer serializer = SerializerProvider.GetEdmTypeSerializer(edmType);
        if (serializer == null)
        {
            throw new SerializationException(
                Error.Format(SRResources.TypeCannotBeSerialized, edmType.ToTraceString()));
        }

        await serializer.WriteObjectInlineAsync(propertyValue, edmType, writer, nestedWriteContext).ConfigureAwait(false);
    }

    /// <summary>
    /// Writing the declared Edm.Untyped properties.
    /// </summary>
    private async Task WriteUntypedPropertiesAsync(SelectExpandNode selectExpandNode, ResourceContext resourceContext, ODataWriter writer)
    {
        Contract.Assert(selectExpandNode != null);
        Contract.Assert(resourceContext != null);
        Contract.Assert(writer != null);

        if (selectExpandNode.SelectedStructuralProperties == null)
        {
            return;
        }

        foreach (IEdmStructuralProperty structuralProperty in selectExpandNode.SelectedStructuralProperties)
        {
            if (structuralProperty.Type == null ||
                (!structuralProperty.Type.IsUntyped() && !structuralProperty.Type.IsCollectionUntyped()))
            {
                continue;
            }

            object propertyValue = CreateUntypedPropertyValue(structuralProperty, resourceContext, out IEdmTypeReference actualType);
            if (propertyValue == null)
            {
                // if we get a null value, it means to skip it.
                continue;
            }

            if (propertyValue is ODataProperty odataProperty)
            {
                await writer.WriteStartAsync(odataProperty).ConfigureAwait(false);
                await writer.WriteEndAsync().ConfigureAwait(false);
                continue;
            }

            // Ok, it's an resource or collection value.
            if (actualType != null && actualType.IsStructuredOrUntyped())
            {
                ODataNestedResourceInfo nestedResourceInfo
                    = CreateUntypedNestedResourceInfo(structuralProperty, propertyValue, actualType, null/*not used now*/, resourceContext);

                if (nestedResourceInfo != null)
                {
                    await writer.WriteStartAsync(nestedResourceInfo).ConfigureAwait(false);
                    await WriteDynamicComplexPropertyAsync(propertyValue, actualType, resourceContext, writer).ConfigureAwait(false);
                    await writer.WriteEndAsync().ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Creates the <see cref="ODataNestedResourceInfo"/> to be written while writing this declared untyped property.
    /// </summary>
    /// <param name="structuralProperty">The declared untyped property for which the nested resource info is being created.</param>
    /// <param name="propertyValue">The declared property real value.</param>
    /// <param name="valueType">The resolved edm type of the declared property real value.</param>
    /// <param name="pathSelectItem">The corresponding sub select item belongs to this untyped property.</param>
    /// <param name="resourceContext">The context for the untyped instance being written.</param>
    /// <returns>The nested resource info to be written. Returns 'null' will omit this untyped serialization.</returns>
    /// <remarks>It enables customer to get more control by overriding this method. </remarks>
    public virtual ODataNestedResourceInfo CreateUntypedNestedResourceInfo(IEdmStructuralProperty structuralProperty,
        object propertyValue, IEdmTypeReference valueType,
        PathSelectItem pathSelectItem, ResourceContext resourceContext)
    {
        if (structuralProperty == null)
        {
            throw Error.ArgumentNull(nameof(structuralProperty));
        }

        if (valueType != null)
        {
            return new ODataNestedResourceInfo
            {
                IsCollection = valueType.IsCollection(),
                Name = structuralProperty.Name
            };
        }

        return null;
    }

    private async Task WriteStreamPropertiesAsync(SelectExpandNode selectExpandNode, ResourceContext resourceContext, ODataWriter writer)
    {
        Contract.Assert(selectExpandNode != null);
        Contract.Assert(resourceContext != null);
        Contract.Assert(writer != null);

        if (selectExpandNode.SelectedStructuralProperties != null)
        {
            IEnumerable<IEdmStructuralProperty> structuralProperties = selectExpandNode.SelectedStructuralProperties;

            foreach (IEdmStructuralProperty structuralProperty in structuralProperties)
            {
                if (structuralProperty.Type != null && structuralProperty.Type.IsStream())
                {
                    ODataStreamPropertyInfo property = CreateStreamProperty(structuralProperty, resourceContext);

                    if (property != null)
                    {
                        await writer.WriteStartAsync(property).ConfigureAwait(false);
                        await writer.WriteEndAsync().ConfigureAwait(false);
                    }
                }
            }
        }
    }

    private async Task WriteComplexPropertiesAsync(SelectExpandNode selectExpandNode, ResourceContext resourceContext, ODataWriter writer)
    {
        Contract.Assert(selectExpandNode != null);
        Contract.Assert(resourceContext != null);
        Contract.Assert(writer != null);

        IDictionary<IEdmStructuralProperty, PathSelectItem> complexProperties = selectExpandNode.SelectedComplexProperties;
        if (complexProperties == null)
        {
            return;
        }

        if (resourceContext.EdmObject != null && resourceContext.SerializerContext.IsDelta)
        {
            IDelta deltaObject = resourceContext.EdmObject as IDelta;
            IEnumerable<string> changedProperties = deltaObject.GetChangedPropertyNames();
            complexProperties = complexProperties.Where(p => changedProperties.Contains(p.Key.Name)).ToDictionary(a => a.Key, a => a.Value);
        }

        foreach (KeyValuePair<IEdmStructuralProperty, PathSelectItem> selectedComplex in complexProperties)
        {
            IEdmStructuralProperty complexProperty = selectedComplex.Key;

            ODataNestedResourceInfo nestedResourceInfo = CreateComplexNestedResourceInfo(complexProperty, selectedComplex.Value, resourceContext);
            if (nestedResourceInfo != null)
            {
                await writer.WriteStartAsync(nestedResourceInfo).ConfigureAwait(false);
                await WriteComplexAndExpandedNavigationPropertyAsync(complexProperty, selectedComplex.Value, resourceContext, writer)
                    .ConfigureAwait(false);
                await writer.WriteEndAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task WriteExpandedNavigationPropertiesAsync(SelectExpandNode selectExpandNode, ResourceContext resourceContext, ODataWriter writer)
    {
        Contract.Assert(resourceContext != null);
        Contract.Assert(writer != null);

        IDictionary<IEdmNavigationProperty, ExpandedNavigationSelectItem> navigationPropertiesToExpand = selectExpandNode.ExpandedProperties;
        if (navigationPropertiesToExpand == null)
        {
            return;
        }

        foreach (KeyValuePair<IEdmNavigationProperty, ExpandedNavigationSelectItem> navPropertyToExpand in navigationPropertiesToExpand)
        {
            IEdmNavigationProperty navigationProperty = navPropertyToExpand.Key;

            ODataNestedResourceInfo navigationLink = CreateNavigationLink(navigationProperty, resourceContext);
            if (navigationLink != null)
            {
                await writer.WriteStartAsync(navigationLink).ConfigureAwait(false);
                await WriteComplexAndExpandedNavigationPropertyAsync(navigationProperty, navPropertyToExpand.Value, resourceContext, writer).ConfigureAwait(false);
                await writer.WriteEndAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task WriteReferencedNavigationPropertiesAsync(SelectExpandNode selectExpandNode, ResourceContext resourceContext, ODataWriter writer)
    {
        Contract.Assert(resourceContext != null);
        Contract.Assert(writer != null);

        IDictionary<IEdmNavigationProperty, ExpandedReferenceSelectItem> referencedPropertiesToExpand = selectExpandNode.ReferencedProperties;
        if (referencedPropertiesToExpand == null)
        {
            return;
        }

        foreach (KeyValuePair<IEdmNavigationProperty, ExpandedReferenceSelectItem> referenced in referencedPropertiesToExpand)
        {
            IEdmNavigationProperty navigationProperty = referenced.Key;

            ODataNestedResourceInfo nestedResourceInfo = CreateNavigationLink(navigationProperty, resourceContext);
            if (nestedResourceInfo != null)
            {
                await writer.WriteStartAsync(nestedResourceInfo).ConfigureAwait(false);
                await WriteComplexAndExpandedNavigationPropertyAsync(navigationProperty, referenced.Value, resourceContext, writer)
                    .ConfigureAwait(false);
                await writer.WriteEndAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task WriteComplexAndExpandedNavigationPropertyAsync(IEdmProperty edmProperty, SelectItem selectItem, ResourceContext resourceContext, ODataWriter writer)
    {
        Contract.Assert(edmProperty != null);
        Contract.Assert(resourceContext != null);
        Contract.Assert(writer != null);

        object propertyValue = resourceContext.GetPropertyValue(edmProperty.Name);

        if (propertyValue == null || propertyValue is NullEdmComplexObject)
        {
            if (edmProperty.Type.IsCollection())
            {
                // A complex or navigation property whose Type attribute specifies a collection, the collection always exists,
                // it may just be empty.
                // If a collection of complex or entities can be related, it is represented as a JSON array. An empty
                // collection of resources (one that contains no resource) is represented as an empty JSON array.
                await writer.WriteStartAsync(new ODataResourceSet
                {
                    TypeName = edmProperty.Type.FullName()
                }).ConfigureAwait(false);
            }
            else
            {
                // If at most one resource can be related, the value is null if no resource is currently related.
                await writer.WriteStartAsync(resource: null).ConfigureAwait(false);
            }

            await writer.WriteEndAsync().ConfigureAwait(false);
        }
        else
        {
            // create the serializer context for the complex and expanded item.
            ODataSerializerContext nestedWriteContext = new ODataSerializerContext(resourceContext, edmProperty, resourceContext.SerializerContext.QueryContext, selectItem);

            // write object.
            IODataEdmTypeSerializer serializer = SerializerProvider.GetEdmTypeSerializer(edmProperty.Type);
            if (serializer == null)
            {
                throw new SerializationException(Error.Format(SRResources.TypeCannotBeSerialized, edmProperty.Type.ToTraceString()));
            }

            await serializer.WriteObjectInlineAsync(propertyValue, edmProperty.Type, writer, nestedWriteContext)
                .ConfigureAwait(false);
        }
    }

    private IEnumerable<ODataNestedResourceInfo> CreateNavigationLinks(
        IEnumerable<IEdmNavigationProperty> navigationProperties, ResourceContext resourceContext)
    {
        Contract.Assert(navigationProperties != null);
        Contract.Assert(resourceContext != null);

        foreach (IEdmNavigationProperty navProperty in navigationProperties)
        {
            ODataNestedResourceInfo navigationLink = CreateNavigationLink(navProperty, resourceContext);
            if (ShouldWriteNavigation(navigationLink, resourceContext))
            {
                yield return navigationLink;
            }
        }
    }

    /// <summary>
    /// Checks whether a navigation link should be written or not. 
    /// </summary>
    /// <param name="navigationLink">The navigation link to be written.</param>
    /// <param name="resourceContext">The resource context for the resource whose navigation link is being written.</param>
    /// <returns>true if navigation link should be written; otherwise false.</returns>
    protected virtual bool ShouldWriteNavigation(ODataNestedResourceInfo navigationLink, ResourceContext resourceContext)
    {
        if (navigationLink?.Url != null || (navigationLink != null && resourceContext.SerializerContext.MetadataLevel == ODataMetadataLevel.Full))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Creates the <see cref="ODataNestedResourceInfo"/> to be written while writing this dynamic complex property.
    /// </summary>
    /// <param name="propertyName">The dynamic property name.</param>
    /// <param name="propertyValue">The dynamic property value.</param>
    /// <param name="edmType">The edm type reference.</param>
    /// <param name="resourceContext">The context for the complex instance being written.</param>
    /// <returns>The nested resource info to be written. Returns 'null' will omit this serialization.</returns>
    /// <remarks>It enables customer to get more control by overriding this method. </remarks>
    public virtual ODataNestedResourceInfo CreateDynamicComplexNestedResourceInfo(string propertyName, object propertyValue, IEdmTypeReference edmType, ResourceContext resourceContext)
    {
        ODataNestedResourceInfo nestedInfo = null;
        if (propertyName != null && edmType != null)
        {
            nestedInfo = new ODataNestedResourceInfo
            {
                IsCollection = edmType.IsCollection(),
                Name = propertyName,
            };
        }

        return nestedInfo;
    }

    /// <summary>
    /// Creates the <see cref="ODataNestedResourceInfo"/> to be written while writing this complex property.
    /// </summary>
    /// <param name="complexProperty">The complex property for which the nested resource info is being created.</param>
    /// <param name="pathSelectItem">The corresponding sub select item belongs to this complex property.</param>
    /// <param name="resourceContext">The context for the complex instance being written.</param>
    /// <returns>The nested resource info to be written. Returns 'null' will omit this complex serialization.</returns>
    /// <remarks>It enables customer to get more control by overriding this method. </remarks>
    public virtual ODataNestedResourceInfo CreateComplexNestedResourceInfo(IEdmStructuralProperty complexProperty, PathSelectItem pathSelectItem, ResourceContext resourceContext)
    {
        if (complexProperty == null)
        {
            throw Error.ArgumentNull(nameof(complexProperty));
        }

        ODataNestedResourceInfo nestedInfo = null;

        if (complexProperty.Type != null)
        {
            nestedInfo = new ODataNestedResourceInfo
            {
                IsCollection = complexProperty.Type.IsCollection(),
                Name = complexProperty.Name
            };
        }

        return nestedInfo;
    }

    /// <summary>
    /// Creates the <see cref="ODataNestedResourceInfo"/> to be written while writing this entity.
    /// </summary>
    /// <param name="navigationProperty">The navigation property for which the navigation link is being created.</param>
    /// <param name="resourceContext">The context for the entity instance being written.</param>
    /// <returns>The navigation link to be written.</returns>
    public virtual ODataNestedResourceInfo CreateNavigationLink(IEdmNavigationProperty navigationProperty, ResourceContext resourceContext)
    {
        if (navigationProperty == null)
        {
            throw Error.ArgumentNull(nameof(navigationProperty));
        }

        if (resourceContext == null)
        {
            throw Error.ArgumentNull(nameof(resourceContext));
        }

        ODataSerializerContext writeContext = resourceContext.SerializerContext;
        IEdmNavigationSource navigationSource = writeContext.NavigationSource;
        ODataNestedResourceInfo navigationLink = null;

        if (navigationProperty.Type != null)
        {
            IEdmTypeReference propertyType = navigationProperty.Type;
            navigationLink = new ODataNestedResourceInfo
            {
                IsCollection = propertyType.IsCollection(),
                Name = navigationProperty.Name,
            };

            if (navigationSource != null)
            {
                IEdmModel model = writeContext.Model;
                NavigationSourceLinkBuilderAnnotation linkBuilder = EdmModelLinkBuilderExtensions.GetNavigationSourceLinkBuilder(model, navigationSource);
                Uri navigationUrl = linkBuilder.BuildNavigationLink(resourceContext, navigationProperty, writeContext.MetadataLevel);

                if (navigationUrl != null)
                {
                    navigationLink.Url = navigationUrl;
                }
            }
        }

        return navigationLink;
    }

    /// <summary>
    /// Creates the <see cref="ODataProperty"/>s to be written while writing this entity.
    /// </summary>
    /// <param name="selectExpandNode">The <see cref="SelectExpandNode" /> to determine the properties to be written</param>
    /// <param name="resourceContext">The context for the entity instance being written.</param>
    /// <returns>The navigation link to be written.</returns>
    /// <returns>ODataProperties to be written</returns>
    private IEnumerable<ODataProperty> CreateStructuralPropertyBag(SelectExpandNode selectExpandNode, ResourceContext resourceContext)
    {
        Contract.Assert(selectExpandNode != null);
        Contract.Assert(resourceContext != null);

        int propertiesCount = (selectExpandNode.SelectedStructuralProperties?.Count ?? 0) + (selectExpandNode.SelectedComputedProperties?.Count ?? 0);
        List<ODataProperty> properties = new List<ODataProperty>(propertiesCount);

        if (selectExpandNode.SelectedStructuralProperties != null)
        {
            IEnumerable<IEdmStructuralProperty> structuralProperties = selectExpandNode.SelectedStructuralProperties;

            if (resourceContext.EdmObject != null && resourceContext.SerializerContext.IsDelta)
            {
                IDelta deltaObject = null;
                if (resourceContext.EdmObject is TypedEdmEntityObject obj)
                {
                    deltaObject = obj.Instance as IDelta;
                }
                else
                {
                    deltaObject = resourceContext.EdmObject as IDelta;
                }

                if (deltaObject != null)
                {
                    IEnumerable<string> changedProperties = deltaObject.GetChangedPropertyNames();
                    structuralProperties = structuralProperties.Where(p => changedProperties.Contains(p.Name) || p.IsKey());
                }
            }

            foreach (IEdmStructuralProperty structuralProperty in structuralProperties)
            {
                if (structuralProperty.Type != null && structuralProperty.Type.IsStream())
                {
                    // skip the stream property, the stream property is written in its own logic
                    continue;
                }

                if (structuralProperty.Type != null &&
                    (structuralProperty.Type.IsUntyped() || structuralProperty.Type.IsCollectionUntyped()))
                {
                    // skip it here, we use a different method to write all 'declared' untyped properties
                    continue;
                }

                ODataProperty property = CreateStructuralProperty(structuralProperty, resourceContext);
                if (property != null)
                {
                    properties.Add(property);
                }
            }
        }

        // Try to add computed properties
        if (selectExpandNode.SelectedComputedProperties != null)
        {
            foreach (string propertyName in selectExpandNode.SelectedComputedProperties)
            {
                ODataProperty property = CreateComputedProperty(propertyName, resourceContext);
                if (property != null)
                {
                    properties.Add(property);
                }
            }
        }

        return properties;
    }

    /// <summary>
    /// Creates the <see cref="ODataProperty"/> to be written for the given resource.
    /// </summary>
    /// <param name="propertyName">The computed property being written.</param>
    /// <param name="resourceContext">The context for the resource instance being written.</param>
    /// <returns>The <see cref="ODataProperty"/> to write.</returns>
    public virtual ODataProperty CreateComputedProperty(string propertyName, ResourceContext resourceContext)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            throw Error.ArgumentNullOrEmpty(nameof(propertyName));
        }

        if (resourceContext == null)
        {
            throw Error.ArgumentNull(nameof(resourceContext));
        }

        // The computed value is from the Linq expression binding.
        object propertyValue = resourceContext.GetPropertyValue(propertyName);
        if (propertyValue == null)
        {
            return new ODataProperty { Name = propertyName, Value = null };
        }

        ODataSerializerContext writeContext = resourceContext.SerializerContext;

        IEdmTypeReference edmTypeReference = resourceContext.SerializerContext.GetEdmType(propertyValue, propertyValue.GetType());
        if (edmTypeReference == null)
        {
            throw Error.NotSupported(SRResources.TypeOfDynamicPropertyNotSupported, propertyValue.GetType().FullName, propertyName);
        }

        IODataEdmTypeSerializer serializer = SerializerProvider.GetEdmTypeSerializer(edmTypeReference);
        if (serializer == null)
        {
            throw new SerializationException(Error.Format(SRResources.TypeCannotBeSerialized, edmTypeReference.FullName()));
        }

        return serializer.CreateProperty(propertyValue, edmTypeReference, propertyName, writeContext);
    }

    /// <summary>
    /// Creates the <see cref="ODataStreamPropertyInfo"/> to be written for the given stream property.
    /// </summary>
    /// <param name="structuralProperty">The EDM structural property being written.</param>
    /// <param name="resourceContext">The context for the entity instance being written.</param>
    /// <returns>The <see cref="ODataStreamPropertyInfo"/> to write.</returns>
    public virtual ODataStreamPropertyInfo CreateStreamProperty(IEdmStructuralProperty structuralProperty, ResourceContext resourceContext)
    {
        if (structuralProperty == null)
        {
            throw Error.ArgumentNull("structuralProperty");
        }

        if (resourceContext == null)
        {
            throw Error.ArgumentNull("resourceContext");
        }

        if (structuralProperty.Type == null || !structuralProperty.Type.IsStream())
        {
            return null;
        }

        if (resourceContext.SerializerContext.MetadataLevel != ODataMetadataLevel.Full)
        {
            return null;
        }

        // TODO: we need to return ODataStreamReferenceValue if
        // 1) If we have the EditLink link builder
        // 2) If we have the ReadLink link builder
        // 3) If we have the Core.AcceptableMediaTypes annotation associated with the Stream property,

        // So far, let's return null and let OData.lib to calculate the ODataStreamReferenceValue by conventions.
        return null;
    }

    /// <summary>
    /// Creates the property value to be written for the given, declared, "Edm.Untyped" or collection property.
    /// </summary>
    /// <param name="structuralProperty">The EDM structural property being written.</param>
    /// <param name="resourceContext">The context for the entity instance being written.</param>
    /// <param name="actualType">The inferred actual type.</param>
    /// <returns>1) return 'null' to skip it.
    /// 2) return 'ODataProperty' to write it as primitive property.
    /// 3) otherwise, return the inferred value and its type.</returns>
    public virtual object CreateUntypedPropertyValue(IEdmStructuralProperty structuralProperty,
        ResourceContext resourceContext, out IEdmTypeReference actualType)
    {
        if (structuralProperty == null)
        {
            throw Error.ArgumentNull(nameof(structuralProperty));
        }

        if (resourceContext == null)
        {
            throw Error.ArgumentNull(nameof(resourceContext));
        }

        actualType = null;
        if (structuralProperty.Type == null ||
            (!structuralProperty.Type.IsUntyped() && !structuralProperty.Type.IsCollectionUntyped()))
        {
            return null;
        }

        // Retrieve the original/raw value from Data source.
        object propertyValue = resourceContext.GetPropertyValue(structuralProperty.Name);
        if (propertyValue == null)
        {
            return new ODataProperty
            {
                Name = structuralProperty.Name,
                Value = ODataNullValueExtensions.NullValue
            };
        }

        Type propertyType = propertyValue.GetType();
        ODataSerializerContext writeContext = resourceContext.SerializerContext;

        // Scenarios:
        // 1) If we can get EdmType from model, Let's use it.
        // 2) If no (aka, we don't have an Edm type associated). So, let's treat it a Untyped.
        actualType = writeContext.GetEdmType(propertyValue, propertyType, true);

        if (actualType.IsStructuredOrUntyped())
        {
            if (TypeHelper.IsEnum(propertyType))
            {
                // we don't have the Edm enum type in the model, let's write it as string.
                return new ODataProperty
                {
                    Name = structuralProperty.Name,

                    // Shall we write the un-declared enum value as full-name string?
                    // So, "Data":"Apple"  => should be ""Data":"Namespace.EnumTypeName.Apple" ?
                    // We keep it simple to write it as string (enum member name), not the full-name string.
                    Value = propertyValue.ToString()
                };
            }

            return propertyValue;
        }

        // Ok, we have the Edm type associated and it's not strctured or untyped.
        // we only handle the 'Primitive', the defined 'Enum' or collection of them.
        IODataEdmTypeSerializer serializer = SerializerProvider.GetEdmTypeSerializer(actualType);
        if (serializer == null)
        {
            throw new SerializationException(
                Error.Format(SRResources.TypeCannotBeSerialized, structuralProperty.Type.FullName()));
        }

        return serializer.CreateProperty(propertyValue, actualType, structuralProperty.Name, writeContext);
    }

    /// <summary>
    /// Creates the <see cref="ODataProperty"/> to be written for the given entity and the structural property.
    /// </summary>
    /// <param name="structuralProperty">The EDM structural property being written.</param>
    /// <param name="resourceContext">The context for the entity instance being written.</param>
    /// <returns>The <see cref="ODataProperty"/> to write.</returns>
    public virtual ODataProperty CreateStructuralProperty(IEdmStructuralProperty structuralProperty, ResourceContext resourceContext)
    {
        if (structuralProperty == null)
        {
            throw Error.ArgumentNull(nameof(structuralProperty));
        }
        if (resourceContext == null)
        {
            throw Error.ArgumentNull(nameof(resourceContext));
        }

        ODataSerializerContext writeContext = resourceContext.SerializerContext;

        IODataEdmTypeSerializer serializer = SerializerProvider.GetEdmTypeSerializer(structuralProperty.Type);
        if (serializer == null)
        {
            throw new SerializationException(
                Error.Format(SRResources.TypeCannotBeSerialized, structuralProperty.Type.FullName()));
        }

        object propertyValue = resourceContext.GetPropertyValue(structuralProperty.Name);

        IEdmTypeReference propertyType = structuralProperty.Type;
        if (propertyValue != null)
        {
            if (!propertyType.IsPrimitive() && !propertyType.IsEnum())
            {
                IEdmTypeReference actualType = writeContext.GetEdmType(propertyValue, propertyValue.GetType());
                if (propertyType != null && propertyType != actualType)
                {
                    propertyType = actualType;
                }
            }
        }

        return serializer.CreateProperty(propertyValue, propertyType, structuralProperty.Name, writeContext);
    }

    private IEnumerable<ODataAction> CreateODataActions(
        IEnumerable<IEdmAction> actions, ResourceContext resourceContext)
    {
        Contract.Assert(actions != null);
        Contract.Assert(resourceContext != null);

        foreach (IEdmAction action in actions)
        {
            ODataAction oDataAction = CreateODataAction(action, resourceContext);
            if (oDataAction != null)
            {
                yield return oDataAction;
            }
        }
    }

    private IEnumerable<ODataFunction> CreateODataFunctions(
        IEnumerable<IEdmFunction> functions, ResourceContext resourceContext)
    {
        Contract.Assert(functions != null);
        Contract.Assert(resourceContext != null);

        foreach (IEdmFunction function in functions)
        {
            ODataFunction oDataFunction = CreateODataFunction(function, resourceContext);
            if (oDataFunction != null)
            {
                yield return oDataFunction;
            }
        }
    }

    /// <summary>
    /// Creates an <see cref="ODataAction" /> to be written for the given action and the entity instance.
    /// </summary>
    /// <param name="action">The OData action.</param>
    /// <param name="resourceContext">The context for the entity instance being written.</param>
    /// <returns>The created action or null if the action should not be written.</returns>
    [SuppressMessage("Microsoft.Usage", "CA2234: Pass System.Uri objects instead of strings", Justification = "This overload is equally good")]
    public virtual ODataAction CreateODataAction(IEdmAction action, ResourceContext resourceContext)
    {
        if (action == null)
        {
            throw Error.ArgumentNull(nameof(action));
        }

        if (resourceContext == null)
        {
            throw Error.ArgumentNull(nameof(resourceContext));
        }

        IEdmModel model = resourceContext.EdmModel;
        OperationLinkBuilder builder = model.GetOperationLinkBuilder(action);

        if (builder == null)
        {
            return null;
        }

        return CreateODataOperation(action, builder, resourceContext) as ODataAction;
    }

    /// <summary>
    /// Creates an <see cref="ODataFunction" /> to be written for the given action and the entity instance.
    /// </summary>
    /// <param name="function">The OData function.</param>
    /// <param name="resourceContext">The context for the entity instance being written.</param>
    /// <returns>The created function or null if the action should not be written.</returns>
    [SuppressMessage("Microsoft.Usage", "CA2234: Pass System.Uri objects instead of strings",
        Justification = "This overload is equally good")]
    [SuppressMessage("Microsoft.Naming", "CA1716: Use function as parameter name", Justification = "Function")]
    public virtual ODataFunction CreateODataFunction(IEdmFunction function, ResourceContext resourceContext)
    {
        if (function == null)
        {
            throw Error.ArgumentNull(nameof(function));
        }

        if (resourceContext == null)
        {
            throw Error.ArgumentNull(nameof(resourceContext));
        }

        IEdmModel model = resourceContext.EdmModel;
        OperationLinkBuilder builder = model.GetOperationLinkBuilder(function);

        if (builder == null)
        {
            return null;
        }

        return CreateODataOperation(function, builder, resourceContext) as ODataFunction;
    }

    private static ODataOperation CreateODataOperation(IEdmOperation operation, OperationLinkBuilder builder, ResourceContext resourceContext)
    {
        Contract.Assert(operation != null);
        Contract.Assert(builder != null);
        Contract.Assert(resourceContext != null);

        ODataMetadataLevel metadataLevel = resourceContext.SerializerContext.MetadataLevel;
        IEdmModel model = resourceContext.EdmModel;

        if (ShouldOmitOperation(operation, builder, metadataLevel))
        {
            return null;
        }

        Uri target = builder.BuildLink(resourceContext);
        if (target == null)
        {
            return null;
        }

        Uri baseUri = new Uri(resourceContext.Request.CreateODataLink(MetadataSegment.Instance));
        Uri metadata = new Uri(baseUri, "#" + CreateMetadataFragment(operation));

        ODataOperation odataOperation;
        if (operation is IEdmAction)
        {
            odataOperation = new ODataAction();
        }
        else
        {
            odataOperation = new ODataFunction();
        }
        odataOperation.Metadata = metadata;

        // Always omit the title in minimal/no metadata modes.
        if (metadataLevel == ODataMetadataLevel.Full)
        {
            EmitTitle(model, operation, odataOperation);
        }

        // Omit the target in minimal/no metadata modes unless it doesn't follow conventions.
        if (!builder.FollowsConventions || metadataLevel == ODataMetadataLevel.Full)
        {
            odataOperation.Target = target;
        }

        return odataOperation;
    }

    internal static void EmitTitle(IEdmModel model, IEdmOperation operation, ODataOperation odataOperation)
    {
        // The title should only be emitted in full metadata.
        OperationTitleAnnotation titleAnnotation = model.GetOperationTitleAnnotation(operation);
        if (titleAnnotation != null)
        {
            odataOperation.Title = titleAnnotation.Title;
        }
        else
        {
            odataOperation.Title = operation.Name;
        }
    }

    internal static string CreateMetadataFragment(IEdmOperation operation)
    {
        // There can only be one entity container in OData V4.
        string actionName = operation.Name;
        string fragment = operation.Namespace + "." + actionName;

        return fragment;
    }

    private static IEdmStructuredType GetODataPathType(ODataSerializerContext serializerContext)
    {
        Contract.Assert(serializerContext != null);
        if (serializerContext.EdmProperty != null)
        {
            // we are in an nested complex or expanded navigation property.
            if (serializerContext.EdmProperty.Type.IsCollection())
            {
                return serializerContext.EdmProperty.Type.AsCollection().ElementType().ToStructuredType();
            }
            else
            {
                return serializerContext.EdmProperty.Type.AsStructured().StructuredDefinition();
            }
        }
        else
        {
            if (serializerContext.ExpandedResource != null)
            {
                // we are in dynamic complex.
                return null;
            }

            IEdmType edmType = null;

            // figure out the type from the navigation source
            if (serializerContext.NavigationSource != null)
            {
                edmType = serializerContext.NavigationSource.EntityType;
                if (edmType.TypeKind == EdmTypeKind.Collection)
                {
                    edmType = (edmType as IEdmCollectionType).ElementType.Definition;
                }
            }

            // figure out the type from the path.
            if (serializerContext.Path != null)
            {
                // Note: The navigation source may be different from the path if the instance has redefined the context
                // (for example, in a flattened delta response)
                if (serializerContext.NavigationSource == null || serializerContext.NavigationSource == serializerContext.Path.GetNavigationSource())
                {
                    edmType = serializerContext.Path.GetEdmType();
                    if (edmType != null && edmType.TypeKind == EdmTypeKind.Collection)
                    {
                        edmType = (edmType as IEdmCollectionType).ElementType.Definition;
                    }
                }
            }

            return edmType as IEdmStructuredType;
        }
    }

    internal static void AddTypeNameAnnotationAsNeeded(ODataResourceBase resource, IEdmStructuredType odataPathType,
        ODataMetadataLevel metadataLevel)
    {
        // ODataLib normally has the caller decide whether or not to serialize properties by leaving properties
        // null when values should not be serialized. The TypeName property is different and should always be
        // provided to ODataLib to enable model validation. A separate annotation is used to decide whether or not
        // to serialize the type name (a null value prevents serialization).

        // Note: In the current version of ODataLib the default behavior likely now matches the requirements for
        // minimal metadata mode. However, there have been behavior changes/bugs there in the past, so the safer
        // option is for this class to take control of type name serialization in minimal metadata mode.

        Contract.Assert(resource != null);

        string typeName = null; // Set null to force the type name not to serialize.

        // Provide the type name to serialize.
        if (!ShouldSuppressTypeNameSerialization(resource, odataPathType, metadataLevel))
        {
            typeName = resource.TypeName;
        }

        resource.TypeAnnotation = new ODataTypeAnnotation(typeName);
    }

    internal static void AddTypeNameAnnotationAsNeededForComplex(ODataResourceBase resource, ODataMetadataLevel metadataLevel)
    {
        // ODataLib normally has the caller decide whether or not to serialize properties by leaving properties
        // null when values should not be serialized. The TypeName property is different and should always be
        // provided to ODataLib to enable model validation. A separate annotation is used to decide whether or not
        // to serialize the type name (a null value prevents serialization).
        Contract.Assert(resource != null);

        // Only add an annotation if we want to override ODataLib's default type name serialization behavior.
        if (ShouldAddTypeNameAnnotationForComplex(metadataLevel))
        {
            string typeName;

            // Provide the type name to serialize (or null to force it not to serialize).
            if (ShouldSuppressTypeNameSerializationForComplex(metadataLevel))
            {
                typeName = null;
            }
            else
            {
                typeName = resource.TypeName;
            }

            resource.TypeAnnotation = new ODataTypeAnnotation(typeName);
        }
    }

    internal static bool ShouldAddTypeNameAnnotationForComplex(ODataMetadataLevel metadataLevel)
    {
        switch (metadataLevel)
        {
            // For complex types, the default behavior matches the requirements for minimal metadata mode, so no
            // annotation is necessary.
            case ODataMetadataLevel.Minimal:
                return false;
            // In other cases, this class must control the type name serialization behavior.
            case ODataMetadataLevel.Full:
            case ODataMetadataLevel.None:
            default: // All values already specified; just keeping the compiler happy.
                return true;
        }
    }

    internal static bool ShouldSuppressTypeNameSerializationForComplex(ODataMetadataLevel metadataLevel)
    {
        Contract.Assert(metadataLevel != ODataMetadataLevel.Minimal);

        switch (metadataLevel)
        {
            case ODataMetadataLevel.None:
                return true;
            case ODataMetadataLevel.Full:
            default: // All values already specified; just keeping the compiler happy.
                return false;
        }
    }

    internal static bool ShouldOmitOperation(IEdmOperation operation, OperationLinkBuilder builder,
        ODataMetadataLevel metadataLevel)
    {
        Contract.Assert(builder != null);

        switch (metadataLevel)
        {
            case ODataMetadataLevel.Minimal:
            case ODataMetadataLevel.None:
                return operation.IsBound && builder.FollowsConventions;

            case ODataMetadataLevel.Full:
            default: // All values already specified; just keeping the compiler happy.
                return false;
        }
    }

    internal static bool ShouldSuppressTypeNameSerialization(ODataResourceBase resource, IEdmStructuredType edmType,
        ODataMetadataLevel metadataLevel)
    {
        Contract.Assert(resource != null);

        switch (metadataLevel)
        {
            case ODataMetadataLevel.None:
                return true;
            case ODataMetadataLevel.Full:
                return false;
            case ODataMetadataLevel.Minimal:
            default: // All values already specified; just keeping the compiler happy.
                string pathTypeName = null;
                if (edmType != null)
                {
                    pathTypeName = edmType.FullTypeName();
                }
                string resourceTypeName = resource.TypeName;
                return string.Equals(resourceTypeName, pathTypeName, StringComparison.Ordinal);
        }
    }

    internal static IEdmStructuredTypeReference GetResourceType(object graph, ODataSerializerContext writeContext)
    {
        Contract.Assert(graph != null);

        IEdmTypeReference edmType = writeContext.GetEdmType(graph, graph.GetType(), true);
        Contract.Assert(edmType != null);

        if (edmType.IsUntyped())
        {
            return edmType.ToStructuredTypeReference();
        }

        if (!edmType.IsStructured())
        {
            throw new SerializationException(
                Error.Format(SRResources.CannotWriteType, typeof(ODataResourceSerializer).Name, edmType.FullName()));
        }

        return edmType.AsStructured();
    }

    private bool IsDeltaCollection(object collection)
    {
        return (collection is IDeltaSet || collection is EdmChangedObjectCollection);
    }
}
