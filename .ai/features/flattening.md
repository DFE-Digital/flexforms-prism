# Feature: Flattening

`Catalogue/TemplateCatalogueBuilder.cs`, `Responses/ResponseParser.cs`, `Flattening/ResponseFlattener.cs`, `ValueInterpreter.cs`, `FactHasher.cs`.

Collections explode to item × field with `parent_field_id`, `item_ordinal`, `nested_path`. Skip form plumbing such as `TaskStatus_*`. Align with Web response JSON.

Golden fixtures: `tests/GovUK.Dfe.FlexForms.Prism.Flattener.Tests/Fixtures/transfer/`. Changing output requires golden update + `ProjectorVersion`.
