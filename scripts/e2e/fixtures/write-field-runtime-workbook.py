import sys
from pathlib import Path
from zipfile import ZipFile
from xml.etree.ElementTree import Element, SubElement, tostring

output = Path(sys.argv[1])
sheets = {
    "survey": [
        ["type", "name", "label", "required", "relevant", "constraint", "calculation", "default"],
        ["text", "name", "Inspection name", "yes", "", "string-length(.) >= 3"],
        ["select_multiple states", "status", "Status", "yes"],
        ["calculate", "total", "Calculated total", "", "", "", "if(selected(${status}, 'open'), 2, 1)"],
        ["begin_repeat", "visits", "Visits"],
        ["text", "note", "Visit note", "yes", "", "", "", "Offline visit one"],
        ["end_repeat"],
        ["image", "photo", "Inspection photo", "", "selected(${status}, 'open')"],
        ["geopoint", "where", "Location", "yes"],
    ],
    "settings": [["form_id", "form_title"], ["field_runtime_inspection", "Field Runtime Inspection"]],
    "choices": [["list_name", "name", "label"], ["states", "open", "Open"], ["states", "closed", "Closed"]],
}
spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main"
doc_rels = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"
package_rels = "http://schemas.openxmlformats.org/package/2006/relationships"
with ZipFile(output, "w") as file:
    content_types = Element("Types", {"xmlns": "http://schemas.openxmlformats.org/package/2006/content-types"})
    SubElement(content_types, "Default", {
        "Extension": "rels", "ContentType": "application/vnd.openxmlformats-package.relationships+xml",
    })
    sheet_type = "application/vnd.openxmlformats-officedocument.spreadsheetml"
    SubElement(content_types, "Override", {"PartName": "/xl/workbook.xml", "ContentType": f"{sheet_type}.sheet.main+xml"})
    root_relationships = Element("Relationships", {"xmlns": package_rels})
    SubElement(root_relationships, "Relationship", {
        "Id": "workbook", "Type": f"{doc_rels}/officeDocument", "Target": "xl/workbook.xml",
    })
    file.writestr("_rels/.rels", tostring(root_relationships))
    workbook = Element("workbook", {"xmlns": spreadsheet, "xmlns:r": doc_rels})
    workbook_sheets = SubElement(workbook, "sheets")
    relationships = Element("Relationships", {"xmlns": package_rels})
    for index, (name, rows) in enumerate(sheets.items(), 1):
        rid = f"s{index}"
        SubElement(workbook_sheets, "sheet", {"name": name, "sheetId": str(index), "r:id": rid})
        SubElement(relationships, "Relationship", {
            "Id": rid, "Type": f"{doc_rels}/worksheet", "Target": f"worksheets/{name}.xml",
        })
        SubElement(content_types, "Override", {
            "PartName": f"/xl/worksheets/{name}.xml", "ContentType": f"{sheet_type}.worksheet+xml",
        })
        worksheet = Element("worksheet", {"xmlns": spreadsheet})
        data = SubElement(worksheet, "sheetData")
        for number, values in enumerate(rows, 1):
            row = SubElement(data, "row", {"r": str(number)})
            for column, value in enumerate(values):
                if value:
                    cell = SubElement(row, "c", {"r": f"{chr(65+column)}{number}", "t": "inlineStr"})
                    SubElement(SubElement(cell, "is"), "t").text = value
        file.writestr(f"xl/worksheets/{name}.xml", tostring(worksheet))
    file.writestr("[Content_Types].xml", tostring(content_types))
    file.writestr("xl/workbook.xml", tostring(workbook))
    file.writestr("xl/_rels/workbook.xml.rels", tostring(relationships))
