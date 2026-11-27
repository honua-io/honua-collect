// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Honua.Field.Core.Field.Forms;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Field.App;

/// <summary>
/// Built-in sample forms so the app launches into a working capture experience.
/// In production these come from the server's form-package service; this keeps
/// the app self-contained for a first run / demo. Field ids map 1:1 to the
/// server's "Offline Field Sites" feature layer so a submission applies cleanly
/// via GeoServices applyEdits.
/// </summary>
public static class SampleForms
{
    /// <summary>Starts an inspection with the defaults required by the Offline Field Sites layer.</summary>
    public static FormSession CreateFieldSiteSession(string recordId, FieldRecord? seed = null)
    {
        var session = FormSession.CreateForNewRecord(FieldSite(), recordId, seedFrom: seed,
            seedDefaults: new Dictionary<string, object?> { ["priority"] = "normal" });
        session.Record.Values["inspection_date"] = session.Record.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture);
        session.Record.Values["sync_version"] = 1;
        session.Record.Values["offline_action"] = "field-capture";
        return session;
    }

    /// <summary>A field-site inspection matching the server's editable layer schema.</summary>
    public static FormDefinition FieldSite() => new()
    {
        FormId = "field-site",
        Name = "Field Site",
        Sections =
        [
            new FormSection
            {
                SectionId = "details",
                Label = "Site details",
                Fields =
                [
                    new FormField { FieldId = "site_name", Label = "Site name", Type = FormFieldType.Text, Required = true },
                    new FormField
                    {
                        FieldId = "status",
                        Label = "Status",
                        Type = FormFieldType.SingleChoice,
                        Required = true,
                        Choices =
                        [
                            new FieldChoice { Value = "open", Label = "New" },
                            new FieldChoice { Value = "in_progress", Label = "In progress" },
                            new FieldChoice { Value = "resolved", Label = "Done" },
                            new FieldChoice { Value = "blocked", Label = "Blocked" },
                        ],
                    },
                    new FormField
                    {
                        FieldId = "priority",
                        Label = "Priority",
                        Required = true,
                        Type = FormFieldType.SingleChoice,
                        Choices =
                        [
                            new FieldChoice { Value = "low", Label = "Low" },
                            new FieldChoice { Value = "normal", Label = "Normal" },
                            new FieldChoice { Value = "high", Label = "High" },
                            new FieldChoice { Value = "critical", Label = "Critical" },
                        ],
                    },
                    new FormField { FieldId = "assigned_to", Label = "Assigned to", Type = FormFieldType.Text },
                    new FormField { FieldId = "notes", Label = "Notes", Type = FormFieldType.Text },
                    new FormField { FieldId = "photo", Label = "Photo", Type = FormFieldType.Photo },
                ],
            },
        ],
    };

    /// <summary>
    /// A demo form exercising every capture widget (barcode, photo, video, audio,
    /// signature, sketch). Used to verify the on-device capture pipelines; it is
    /// not tied to a server layer.
    /// </summary>
    public static FormDefinition CaptureKit() => new()
    {
        FormId = "capture-kit",
        Name = "Capture Kit",
        Sections =
        [
            new FormSection
            {
                SectionId = "media",
                Label = "Capture widgets",
                Fields =
                [
                    new FormField { FieldId = "asset_tag", Label = "Asset tag (scan)", Type = FormFieldType.Barcode, HelpText = "Scan a barcode or QR code" },
                    new FormField { FieldId = "photo", Label = "Photo", Type = FormFieldType.Photo },
                    new FormField { FieldId = "video", Label = "Video", Type = FormFieldType.Video },
                    new FormField { FieldId = "audio", Label = "Voice note", Type = FormFieldType.Audio },
                    new FormField { FieldId = "signature", Label = "Signature", Type = FormFieldType.Signature },
                    new FormField { FieldId = "sketch", Label = "Sketch", Type = FormFieldType.Sketch },
                ],
            },
        ],
    };

    /// <summary>
    /// A demo form exercising the expression engine (SDK 1.2.0): an arithmetic
    /// calculated field, a constraint expression, and a boolean relevance rule
    /// that reacts to the calculated total.
    /// </summary>
    public static FormDefinition SmartForm() => new()
    {
        FormId = "smart-form",
        Name = "Smart Form",
        Sections =
        [
            new FormSection
            {
                SectionId = "order",
                Label = "Order",
                Fields =
                [
                    new FormField { FieldId = "quantity", Label = "Quantity", Type = FormFieldType.Numeric, Required = true },
                    new FormField { FieldId = "unit_price", Label = "Unit price", Type = FormFieldType.Numeric, Required = true },
                    // Rich arithmetic — impossible with the old concat/sum-only evaluator.
                    new FormField { FieldId = "total", Label = "Total", Type = FormFieldType.Calculated, CalculatedExpression = "$quantity * $unit_price" },
                    // Constraint expression: empty, or exactly 5 characters.
                    new FormField
                    {
                        FieldId = "coupon",
                        Label = "Coupon (blank or 5 chars)",
                        Type = FormFieldType.Text,
                        Validation = new FieldValidationRule
                        {
                            ConstraintExpression = "$coupon = '' or len($coupon) = 5",
                            ConstraintMessage = "Coupon must be blank or exactly 5 characters.",
                        },
                    },
                    // Boolean relevance reacting to the calculated total.
                    new FormField { FieldId = "approver", Label = "Approver (shown when total > 100)", Type = FormFieldType.Text, RelevanceExpression = "$total > 100" },
                ],
            },
        ],
    };
}
