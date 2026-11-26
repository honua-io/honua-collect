import sys
from pathlib import Path
from zipfile import ZipFile
from xml.etree.ElementTree import Element, SubElement, tostring

output = Path(sys.argv[1])
sheets = {
    "survey": [
        ["type", "name", "label", "required", "relevant", "constraint", "calculation"],
        ["text", "name", "Inspection name", "yes", "", "string-length(.) >= 3"],
        ["select_multiple states", "status", "Status", "yes"],
        ["calculate", "total", "Calculated total", "", "", "", "if(selected(${status}, 'open'), 2, 1)"],
        ["begin_repeat", "visits", "Visits"],
        ["text", "note", "Visit note", "yes"],
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
    workbook = Element("workbook", {"xmlns": spreadsheet, "xmlns:r": doc_rels})
    workbook_sheets = SubElement(workbook, "sheets")
    relationships = Element("Relationships", {"xmlns": package_rels})
    for index, (name, rows) in enumerate(sheets.items(), 1):
        rid = f"s{index}"
        SubElement(workbook_sheets, "sheet", {"name": name, "sheetId": str(index), "r:id": rid})
        SubElement(relationships, "Relationship", {
            "Id": rid, "Type": f"{doc_rels}/worksheet", "Target": f"worksheets/{name}.xml",
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
    file.writestr("xl/workbook.xml", tostring(workbook))
    file.writestr("xl/_rels/workbook.xml.rels", tostring(relationships))
