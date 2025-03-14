//-----------------------------------------------------------------------------
// <copyright file="PropertyRoutingConventionTests.cs" company=".NET Foundation">
//      Copyright (c) .NET Foundation and Contributors. All rights reserved.
//      See License.txt in the project root for license information.
// </copyright>
//------------------------------------------------------------------------------

using System;
using System.Linq;
using System.Threading;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.OData.Routing.Conventions;
using Microsoft.AspNetCore.OData.TestCommon;
using Microsoft.AspNetCore.OData.Tests.Commons;
using Microsoft.AspNetCore.OData.Tests.Extensions;
using Microsoft.OData.Edm;
using Xunit;

namespace Microsoft.AspNetCore.OData.Tests.Routing.Conventions;

public class PropertyRoutingConventionTests
{
    private static PropertyRoutingConvention PropertyConvention = ConventionHelpers.CreateConvention<PropertyRoutingConvention>();
    private static IEdmModel EdmModel = GetEdmModel();

    [Fact]
    public void AppliesToActionOnPropertyRoutingConvention_Throws_Context()
    {
        // Arrange
        PropertyRoutingConvention convention = new PropertyRoutingConvention();

        // Act & Assert
        ExceptionAssert.ThrowsArgumentNull(() => convention.AppliesToController(null), "context");
        ExceptionAssert.ThrowsArgumentNull(() => convention.AppliesToAction(null), "context");
    }

    [Theory]
    [InlineData(typeof(CustomersController), true)]
    [InlineData(typeof(MeController), true)]
    [InlineData(typeof(UnknownController), false)]
    public void AppliesToControllerReturnsExpectedForController(Type controllerType, bool expected)
    {
        // Arrange
        ControllerModel controller = ControllerModelHelpers.BuildControllerModel(controllerType);
        ODataControllerActionContext context = ODataControllerActionContextHelpers.BuildContext(string.Empty, EdmModel, controller);

        // Act
        bool actual = PropertyConvention.AppliesToController(context);

        // Assert
        Assert.Equal(expected, actual);
    }

    public static TheoryDataSet<Type, string, string[]> PropertyRoutingConventionTestData
    {
        get
        {
            return new TheoryDataSet<Type, string, string[]>()
            {
                // Get
                {
                    typeof(CustomersController),
                    "GetName",
                    new[]
                    {
                        "/Customers({key})/Name",
                        "/Customers/{key}/Name",
                        "/Customers({key})/Name/$value",
                        "/Customers/{key}/Name/$value"
                    }
                },
                { typeof(MeController), "GetName", new[] { "/Me/Name", "/Me/Name/$value" } },
                {
                    typeof(CustomersController),
                    "GetEmails",
                    new[]
                    {
                        "/Customers({key})/Emails",
                        "/Customers/{key}/Emails",
                        "/Customers({key})/Emails/$count",
                        "/Customers/{key}/Emails/$count"
                    }
                },
                { typeof(MeController), "GetEmails", new[] { "/Me/Emails", "/Me/Emails/$count" } },

                // Get complex property
                { typeof(CustomersController), "GetAddress", new[] { "/Customers({key})/Address", "/Customers/{key}/Address" } },
                { typeof(MeController), "GetAddress", new[] { "/Me/Address" } },
                {
                    typeof(CustomersController),
                    "GetLocations",
                    new[]
                    {
                        "/Customers({key})/Locations",
                        "/Customers/{key}/Locations",
                        "/Customers({key})/Locations/$count",
                        "/Customers/{key}/Locations/$count"
                    }
                },
                { typeof(MeController), "GetLocations", new[] { "/Me/Locations", "/Me/Locations/$count" } },

                // Post
                { typeof(CustomersController), "PostToEmails", new[] { "/Customers({key})/Emails", "/Customers/{key}/Emails" } },
                { typeof(MeController), "PostToEmails", new[] { "/Me/Emails" } },

                // Put, Patch, Delete
                { typeof(CustomersController), "PutToName", new[] { "/Customers({key})/Name", "/Customers/{key}/Name" } },
                { typeof(CustomersController), "PatchToName", new[] { "/Customers({key})/Name", "/Customers/{key}/Name" } },
                { typeof(CustomersController), "DeleteToName", new[] { "/Customers({key})/Name", "/Customers/{key}/Name" } },
                { typeof(MeController), "PutToName", new[] { "/Me/Name" } },
                { typeof(MeController), "PatchToName", new[] { "/Me/Name" } },
                { typeof(MeController), "DeleteToName", new[] { "/Me/Name" } },

                // with type cast
                {
                    typeof(CustomersController),
                    "GetSubAddressFromVipCustomer",
                    new[]
                    {
                        "/Customers({key})/NS.VipCustomer/SubAddress",
                        "/Customers/{key}/NS.VipCustomer/SubAddress"
                    }
                },
                {
                    typeof(CustomersController),
                    "PutToLocationsOfUsAddress",
                    new[]
                    {
                        "/Customers({key})/Locations/NS.UsAddress",
                        "/Customers/{key}/Locations/NS.UsAddress"
                    }
                },
                {
                    typeof(CustomersController),
                    "PatchToSubAddressOfCnAddressFromVipCustomer",
                    new[]
                    {
                        "/Customers({key})/NS.VipCustomer/SubAddress/NS.CnAddress",
                        "/Customers/{key}/NS.VipCustomer/SubAddress/NS.CnAddress"
                    }
                },
                {
                    typeof(MeController),
                    "PutToSubAddressOfCnAddressFromVipCustomer",
                    new[]
                    {
                        "/Me/NS.VipCustomer/SubAddress/NS.CnAddress"
                    }
                },
                {
                    typeof(MeController),
                    "PostToSubLocationsOfUsAddressFromVipCustomer",
                    new[]
                    {
                        "/Me/NS.VipCustomer/SubLocations/NS.UsAddress"
                    }
                },
                {
                    typeof(MeController),
                    "GetSubLocationsOfUsAddressFromVipCustomer",
                    new[]
                    {
                        "/Me/NS.VipCustomer/SubLocations/NS.UsAddress",
                        "/Me/NS.VipCustomer/SubLocations/NS.UsAddress/$count"
                    }
                }
            };
        }
    }

    [Theory]
    [MemberData(nameof(PropertyRoutingConventionTestData))]
    public void PropertyRoutingConventionTestDataRunsAsExpected(Type controllerType, string actionName, string[] templates)
    {
        // Arrange
        ControllerModel controller = ControllerModelHelpers.BuildControllerModel(controllerType, actionName);
        ActionModel action = controller.Actions.First();

        ODataControllerActionContext context = ODataControllerActionContextHelpers.BuildContext(string.Empty, EdmModel, controller);
        context.Action = action;

        // Act
        bool returnValue = PropertyConvention.AppliesToAction(context);
        Assert.True(returnValue);

        // Assert
        Assert.Equal(templates.Length, action.Selectors.Count);
        Assert.Equal(templates, action.Selectors.Select(s => s.AttributeRouteModel.Template));
    }

    [Theory]
    [InlineData(typeof(MeController), "GetEmails", 1, false)]
    [InlineData(typeof(MeController), "GetEmails", 2, true)]
    [InlineData(typeof(MeController), "GetSubLocationsOfUsAddressFromVipCustomer", 1, false)]
    [InlineData(typeof(MeController), "GetSubLocationsOfUsAddressFromVipCustomer", 2, true)]
    public void PropertyRoutingConventionAddDollarCountAsExpectedBasedOnConfiguration(Type controllerType, string actionName, int expectCount, bool enableDollarCount)
    {
        // Arrange
        ControllerModel controller = ControllerModelHelpers.BuildControllerModel(controllerType, actionName);
        ActionModel action = controller.Actions.First();

        ODataControllerActionContext context = ODataControllerActionContextHelpers.BuildContext(string.Empty, EdmModel, controller);
        context.Action = action;
        context.Options.RouteOptions.EnableDollarCountRouting = enableDollarCount;

        // Act
        bool returnValue = PropertyConvention.AppliesToAction(context);
        Assert.True(returnValue);

        // Assert
        Assert.Equal(expectCount, action.Selectors.Count);
        if (enableDollarCount)
        {
            Assert.Contains("/$count", string.Join(",", action.Selectors.Select(s => s.AttributeRouteModel.Template)));
        }
        else
        {
            Assert.DoesNotContain("/$count", string.Join(",", action.Selectors.Select(s => s.AttributeRouteModel.Template)));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void PropertyRoutingConventionOnSingletonWithoutKeyDefinedOnEntityTypeAsExpectedBasedOnConfiguration(int paramerterCount)
    {
        // Arrange
        ControllerModel controller = ControllerModelHelpers.BuildControllerModelWithAllActions<MainSupplierController>();
        ActionModel action = controller.Actions.First(a => a.ActionName == "GetName" && a.Parameters.Count == paramerterCount);

        ODataControllerActionContext context = ODataControllerActionContextHelpers.BuildContext(string.Empty, EdmModel, controller);
        context.Action = action;

        // Act
        bool returnValue = PropertyConvention.AppliesToAction(context);
        Assert.True(returnValue);

        Assert.Equal(["/MainSupplier/Name", "/MainSupplier/Name/$value"], action.Selectors.Select(s => s.AttributeRouteModel.Template));
    }

    [Theory]
    [InlineData("PostToName")]
    [InlineData("Get")]
    [InlineData("PostToSubAddressOfUsAddressFromVipCustomer")]
    [InlineData("GetSubAddressFrom")]
    [InlineData("PostToSubAddressFrom")]
    [InlineData("PutToSubAddressFrom")]
    [InlineData("PatchToSubAddressFrom")]
    [InlineData("DeleteToSubAddressFrom")]
    [InlineData("GetSubAddressOfUsAddressFrom")]
    [InlineData("PostToSubAddressOfUsAddressFrom")]
    [InlineData("PutToSubAddressOfUsAddressFrom")]
    [InlineData("PatchToSubAddressOfUsAddressFrom")]
    [InlineData("DeleteToSubAddressOfUsAddressFrom")]
    [InlineData("GetAddressOf")]
    [InlineData("PostToLocationsOf")]
    [InlineData("PutToAddressOf")]
    [InlineData("PatchToAddressOf")]
    public void PropertyRoutingConventionDoesNothingForNotSupportedAction(string actionName)
    {
        // Arrange
        ControllerModel controller = ControllerModelHelpers.BuildControllerModel<AnotherCustomersController>(actionName);
        ActionModel action = controller.Actions.First();

        ODataControllerActionContext context = ODataControllerActionContextHelpers.BuildContext(string.Empty, EdmModel, controller);
        context.Action = controller.Actions.First();

        // Act
        bool returnValue = PropertyConvention.AppliesToAction(context);
        Assert.False(returnValue);

        // Assert
        SelectorModel selector = Assert.Single(action.Selectors);
        Assert.Null(selector.AttributeRouteModel);
    }

    private static IEdmModel GetEdmModel()
    {
        EdmModel model = new EdmModel();

        // Address
        EdmComplexType address = new EdmComplexType("NS", "Address");
        address.AddStructuralProperty("Street", EdmPrimitiveTypeKind.String);
        model.AddElement(address);

        // CnAddess
        EdmComplexType cnAddress = new EdmComplexType("NS", "CnAddress", address);
        cnAddress.AddStructuralProperty("Postcode", EdmPrimitiveTypeKind.String);
        model.AddElement(cnAddress);

        // UsAddress
        EdmComplexType usAddress = new EdmComplexType("NS", "UsAddress", address);
        usAddress.AddStructuralProperty("Zipcode", EdmPrimitiveTypeKind.Int32);
        model.AddElement(usAddress);

        // Customer
        EdmEntityType customer = new EdmEntityType("NS", "Customer");
        customer.AddKeys(customer.AddStructuralProperty("Id", EdmPrimitiveTypeKind.Int32));
        customer.AddStructuralProperty("Name", EdmPrimitiveTypeKind.String);
        customer.AddStructuralProperty("Emails", new EdmCollectionTypeReference(new EdmCollectionType(EdmCoreModel.Instance.GetPrimitive(EdmPrimitiveTypeKind.String, false))));
        customer.AddStructuralProperty("Address", new EdmComplexTypeReference(address, false));
        customer.AddStructuralProperty("Locations", new EdmCollectionTypeReference(new EdmCollectionType(new EdmComplexTypeReference(address, false))));
        model.AddElement(customer);

        // VipCustomer
        EdmEntityType vipCustomer = new EdmEntityType("NS", "VipCustomer", customer);
        vipCustomer.AddStructuralProperty("SubAddress", new EdmComplexTypeReference(address, false));
        vipCustomer.AddStructuralProperty("SubLocations", new EdmCollectionTypeReference(new EdmCollectionType(new EdmComplexTypeReference(address, false))));
        model.AddElement(vipCustomer);

        EdmEntityContainer container = new EdmEntityContainer("NS", "Default");
        container.AddEntitySet("Customers", customer);
        container.AddEntitySet("AnotherCustomers", customer);
        container.AddSingleton("Me", customer);
        model.AddElement(container);

        // Let's build a singleton using an entity type without a key.
        // So 'Supplier' doesn't contain a key by design.
        EdmEntityType supplier = new EdmEntityType("NS", "Supplier");
        supplier.AddStructuralProperty("Name", EdmPrimitiveTypeKind.String);
        model.AddElement(supplier);
        container.AddSingleton("MainSupplier", supplier);
        return model;
    }

    private class CustomersController
    {
        public void GetName(int key, CancellationToken cancellation)
        { }

        public void GetAddress(int key, CancellationToken cancellation)
        { }

        public void GetLocations(int key)
        { }

        public void GetEmails(int key)
        { }

        public void PutToName(int key)
        { }

        public void PatchToName(CancellationToken cancellation, int key)
        { }

        public void DeleteToName(int key)
        { }

        public void PostToEmails(int key)
        { }

        public void GetSubAddressFromVipCustomer(int key)
        { }

        public void PutToLocationsOfUsAddress(int key)
        { }

        // PATCH ~/Customers(1)/NS.VipCustomer/SubAddress/NS.CnAddress
        public void PatchToSubAddressOfCnAddressFromVipCustomer(int key)
        { }
    }

    private class MeController
    {
        public void GetName(CancellationToken cancellation)
        { }

        public void GetAddress()
        { }

        public void GetEmails()
        { }

        public void GetLocations()
        { }

        public void PutToName()
        { }

        public void PatchToName(CancellationToken cancellation)
        { }

        public void DeleteToName()
        { }

        public void PostToEmails()
        { }

        // Get ~/Me/NS.VipCustomer/SubAddress/CN.UsAddress
        public void GetSubLocationsOfUsAddressFromVipCustomer()
        { }

        // PATCH ~/Me/NS.VipCustomer/SubAddress/CN.CnAddress
        public void PutToSubAddressOfCnAddressFromVipCustomer()
        { }

        // Post ~/Me/NS.VipCustomer/SubAddress/CN.UsAddress
        public void PostToSubLocationsOfUsAddressFromVipCustomer()
        { }
    }

    private class MainSupplierController
    {
        public void GetName()
        { }

        public void GetName(int key)
        {
            // Be noted, the type of 'MainSupplier' doesn't contain a key.
            // So, conventional routing doesn't care about the parameters.
        }
    }

    private class AnotherCustomersController
    {
        public void PostToName(string keyLastName, string keyFirstName)
        { }

        public void Get(int key)
        { }

        // Post to non-collection is not allowed.
        public void PostToSubAddressOfUsAddressFromVipCustomer()
        { }

        public void GetSubAddressFrom(int key)
        {
        }

        public void PostToSubAddressFrom(int key)
        {
        }

        public void PutToSubAddressFrom(int key)
        {
        }

        public void PatchToSubAddressFrom(int key)
        {
        }

        public void DeleteToSubAddressFrom(int key)
        {
        }

        public void GetSubAddressOfUsAddressFrom(int key)
        {
        }

        public void PostToSubAddressOfUsAddressFrom(int key)
        {
        }

        public void PutToSubAddressOfUsAddressFrom(int key)
        {
        }

        public void PatchToSubAddressOfUsAddressFrom(int key)
        {
        }

        public void DeleteToSubAddressOfUsAddressFrom(int key)
        {
        }

        public void GetAddressOf(int key)
        {
        }

        public void PostToLocationsOf(int key)
        {
        }

        public void PutToAddressOf(int key)
        {
        }

        public void PatchToAddressOf(int key)
        {
        }
    }

    private class UnknownController
    { }
}
