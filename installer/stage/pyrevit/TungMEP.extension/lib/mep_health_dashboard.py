# -*- coding: utf-8 -*-
"""Read-only MEP model health checks and pyRevit HTML dashboard."""

from __future__ import print_function

import collections
import datetime
import time

from pyrevit import DB, forms, revit, script


SCOPE_PROJECT = u"Toàn bộ dự án"
SCOPE_VIEW = u"View đang mở"
SCOPE_SELECTION = u"Đối tượng đang chọn"

CHECK_TAGS = u"Kiểm tra thiếu tag trong view hiện tại"
CHECK_WARNINGS = u"Bao gồm Revit warnings liên quan MEP"
CHECK_UNUSED_EQUIPMENT = u"Báo connector thiết bị chưa sử dụng"

SEVERITY_ORDER = {"Critical": 0, "Warning": 1, "FYI": 2}
SEVERITY_COLORS = {
    "Critical": "#d83b32",
    "Warning": "#f2a900",
    "FYI": "#2878b5",
}

CHECK_NAMES = {
    "OPEN_CONNECTOR": u"Connector hở",
    "MISSING_SYSTEM": u"Thiếu System Type",
    "INVALID_SIZE": u"Kích thước không hợp lệ",
    "HIGH_OFFSET": u"Cao độ/offset bất thường",
    "MISSING_TAG": u"Thiếu tag trong view",
    "REVIT_WARNING": u"Revit warning liên quan MEP",
}


def _safe_bic(name):
    try:
        return getattr(DB.BuiltInCategory, name)
    except Exception:
        return None


def _category_id_value(category_or_id):
    try:
        element_id = category_or_id.Id
    except Exception:
        element_id = category_or_id
    try:
        return int(element_id.Value)
    except Exception:
        try:
            return int(element_id.IntegerValue)
        except Exception:
            return None


def _build_category_specs():
    # name, display name, discipline, role, should-check-for-tag
    definitions = [
        ("OST_PipeCurves", u"Pipes", "Piping", "curve", True),
        ("OST_FlexPipeCurves", u"Flex Pipes", "Piping", "curve", True),
        ("OST_PipeFitting", u"Pipe Fittings", "Piping", "fitting", False),
        ("OST_PipeAccessory", u"Pipe Accessories", "Piping", "accessory", True),
        ("OST_PlumbingFixtures", u"Plumbing Fixtures", "Piping", "equipment", True),
        ("OST_Sprinklers", u"Sprinklers", "Piping", "equipment", True),
        ("OST_DuctCurves", u"Ducts", "HVAC", "curve", True),
        ("OST_FlexDuctCurves", u"Flex Ducts", "HVAC", "curve", True),
        ("OST_DuctFitting", u"Duct Fittings", "HVAC", "fitting", False),
        ("OST_DuctAccessory", u"Duct Accessories", "HVAC", "accessory", True),
        ("OST_DuctTerminal", u"Air Terminals", "HVAC", "equipment", True),
        ("OST_MechanicalEquipment", u"Mechanical Equipment", "MEP", "equipment", True),
        ("OST_Conduit", u"Conduits", "Electrical", "curve", True),
        ("OST_ConduitFitting", u"Conduit Fittings", "Electrical", "fitting", False),
        ("OST_CableTray", u"Cable Trays", "Electrical", "curve", True),
        ("OST_CableTrayFitting", u"Cable Tray Fittings", "Electrical", "fitting", False),
        ("OST_ElectricalEquipment", u"Electrical Equipment", "Electrical", "equipment", True),
        ("OST_ElectricalFixtures", u"Electrical Fixtures", "Electrical", "equipment", True),
        ("OST_LightingFixtures", u"Lighting Fixtures", "Electrical", "equipment", True),
        ("OST_FireAlarmDevices", u"Fire Alarm Devices", "Electrical", "equipment", True),
    ]
    specs = []
    by_id = {}
    for builtin_name, label, discipline, role, taggable in definitions:
        bic = _safe_bic(builtin_name)
        if bic is None:
            continue
        category_id = _category_id_value(DB.ElementId(bic))
        if category_id is None:
            continue
        spec = {
            "bic": bic,
            "builtin_name": builtin_name,
            "label": label,
            "discipline": discipline,
            "role": role,
            "taggable": taggable,
            "category_id": category_id,
        }
        specs.append(spec)
        by_id[category_id] = spec
    return specs, by_id


CATEGORY_SPECS, CATEGORY_BY_ID = _build_category_specs()


class HealthIssue(object):
    def __init__(
        self,
        severity,
        code,
        elements,
        category,
        element_type,
        level,
        system,
        detail,
        suggestion,
    ):
        self.severity = severity
        self.code = code
        self.elements = list(elements or [])
        self.category = category or u"—"
        self.element_type = element_type or u"—"
        self.level = level or u"—"
        self.system = system or u"—"
        self.detail = detail or u""
        self.suggestion = suggestion or u""


class HealthReport(object):
    def __init__(self, scope_name):
        self.scope_name = scope_name
        self.elements = []
        self.issues = []
        self.category_counts = collections.defaultdict(int)
        self.level_counts = collections.defaultdict(int)
        self.system_counts = collections.defaultdict(int)
        self.connector_owners = 0
        self.connectors_checked = 0
        self.tag_candidates = 0
        self.revit_warnings_checked = 0
        self.elapsed_seconds = 0.0
        self.notes = []

    def add_issue(self, issue):
        self.issues.append(issue)

    def count_severity(self, severity):
        return sum(1 for issue in self.issues if issue.severity == severity)

    def count_code(self, code):
        return sum(1 for issue in self.issues if issue.code == code)


def _is_invalid_id(element_id):
    if element_id is None:
        return True
    value = _category_id_value(element_id)
    invalid = _category_id_value(DB.ElementId.InvalidElementId)
    return value is None or value == invalid


def _category_spec(element):
    try:
        return CATEGORY_BY_ID.get(_category_id_value(element.Category))
    except Exception:
        return None


def _collector(document, category_spec, view_id=None):
    try:
        if view_id is None:
            result = DB.FilteredElementCollector(document)
        else:
            result = DB.FilteredElementCollector(document, view_id)
        return list(
            result.OfCategory(category_spec["bic"])
            .WhereElementIsNotElementType()
            .ToElements()
        )
    except Exception:
        return []


def _collect_elements(document, scope_name, active_view, selection_ids):
    elements = []
    seen = set()

    if scope_name == SCOPE_SELECTION:
        for element_id in selection_ids:
            try:
                element = document.GetElement(element_id)
            except Exception:
                element = None
            if element is None or _category_spec(element) is None:
                continue
            value = _category_id_value(element.Id)
            if value not in seen:
                seen.add(value)
                elements.append(element)
        return elements

    view_id = active_view.Id if scope_name == SCOPE_VIEW else None
    for spec in CATEGORY_SPECS:
        for element in _collector(document, spec, view_id):
            value = _category_id_value(element.Id)
            if value in seen:
                continue
            seen.add(value)
            elements.append(element)
    return elements


def _get_parameter(element, builtin_name):
    try:
        builtin = getattr(DB.BuiltInParameter, builtin_name)
        return element.get_Parameter(builtin)
    except Exception:
        return None


def _parameter_display(parameter):
    if parameter is None:
        return u""
    try:
        text = parameter.AsValueString()
        if text:
            return text
    except Exception:
        pass
    try:
        text = parameter.AsString()
        if text:
            return text
    except Exception:
        pass
    return u""


def _element_type_name(document, element):
    try:
        element_type = document.GetElement(element.GetTypeId())
        if element_type is not None:
            family_name = getattr(element_type, "FamilyName", u"")
            type_name = getattr(element_type, "Name", u"")
            if family_name and type_name:
                return u"{0}: {1}".format(family_name, type_name)
            if type_name:
                return type_name
    except Exception:
        pass
    try:
        return element.Name
    except Exception:
        return element.GetType().Name


def _level_name(document, element):
    candidate_ids = []
    for property_name in ("LevelId", "ReferenceLevel"):
        try:
            value = getattr(element, property_name)
            if isinstance(value, DB.Level):
                return value.Name
            candidate_ids.append(value)
        except Exception:
            pass
    for builtin_name in (
        "FAMILY_LEVEL_PARAM",
        "INSTANCE_REFERENCE_LEVEL_PARAM",
        "RBS_START_LEVEL_PARAM",
        "SCHEDULE_LEVEL_PARAM",
    ):
        parameter = _get_parameter(element, builtin_name)
        if parameter is not None:
            try:
                candidate_ids.append(parameter.AsElementId())
            except Exception:
                pass
    for level_id in candidate_ids:
        if _is_invalid_id(level_id):
            continue
        try:
            level = document.GetElement(level_id)
            if isinstance(level, DB.Level):
                return level.Name
        except Exception:
            pass
    return u"—"


def _system_name(document, element, spec):
    try:
        mep_system = getattr(element, "MEPSystem", None)
        if mep_system is not None and mep_system.Name:
            return mep_system.Name
    except Exception:
        pass

    if spec["discipline"] == "Piping":
        parameter_names = ("RBS_PIPING_SYSTEM_TYPE_PARAM", "RBS_SYSTEM_NAME_PARAM")
    elif spec["discipline"] == "HVAC":
        parameter_names = ("RBS_DUCT_SYSTEM_TYPE_PARAM", "RBS_SYSTEM_NAME_PARAM")
    else:
        parameter_names = ("RBS_SYSTEM_NAME_PARAM",)

    for builtin_name in parameter_names:
        parameter = _get_parameter(element, builtin_name)
        if parameter is None:
            continue
        try:
            system_type_id = parameter.AsElementId()
            if not _is_invalid_id(system_type_id):
                system_type = document.GetElement(system_type_id)
                if system_type is not None and system_type.Name:
                    return system_type.Name
        except Exception:
            pass
        display = _parameter_display(parameter)
        if display and display.strip():
            return display.strip()

    manager = _connector_manager(element)
    if manager is not None:
        try:
            for connector in manager.Connectors:
                system = getattr(connector, "MEPSystem", None)
                if system is not None and system.Name:
                    return system.Name
        except Exception:
            pass
    return u"—"


def _connector_manager(element):
    try:
        manager = getattr(element, "ConnectorManager", None)
        if manager is not None:
            return manager
    except Exception:
        pass
    try:
        mep_model = getattr(element, "MEPModel", None)
        if mep_model is not None:
            return mep_model.ConnectorManager
    except Exception:
        pass
    return None


def _is_physical_connector(connector):
    try:
        connector_type = str(connector.ConnectorType)
        if connector_type in ("Logical", "Reference"):
            return False
    except Exception:
        pass
    try:
        if str(connector.Domain) == "DomainUndefined":
            return False
    except Exception:
        pass
    return True


def _connector_state(element):
    manager = _connector_manager(element)
    if manager is None:
        return 0, 0
    checked = 0
    open_count = 0
    try:
        for connector in manager.Connectors:
            if not _is_physical_connector(connector):
                continue
            checked += 1
            try:
                if not connector.IsConnected:
                    open_count += 1
            except Exception:
                pass
    except Exception:
        return 0, 0
    return checked, open_count


def _system_is_missing(system_name):
    if not system_name or system_name == u"—":
        return True
    value = system_name.strip().lower()
    return value in (
        "unassigned",
        "undefined",
        "not assigned",
        u"chưa gán",
        u"không xác định",
    )


def _curve_size_problem(element, spec):
    if spec["discipline"] == "Piping":
        names = ("RBS_PIPE_DIAMETER_PARAM",)
    elif spec["discipline"] == "HVAC":
        names = (
            "RBS_CURVE_DIAMETER_PARAM",
            "RBS_CURVE_WIDTH_PARAM",
            "RBS_CURVE_HEIGHT_PARAM",
        )
    elif spec["builtin_name"] == "OST_Conduit":
        names = ("RBS_CONDUIT_DIAMETER_PARAM",)
    elif spec["builtin_name"] == "OST_CableTray":
        names = ("RBS_CABLETRAY_WIDTH_PARAM", "RBS_CABLETRAY_HEIGHT_PARAM")
    else:
        return None

    values = []
    for builtin_name in names:
        parameter = _get_parameter(element, builtin_name)
        if parameter is None:
            continue
        try:
            if parameter.StorageType == DB.StorageType.Double:
                values.append((builtin_name, parameter.AsDouble()))
        except Exception:
            pass
    if values and any(value <= 1.0e-9 for _, value in values):
        return u"Một hoặc nhiều thông số kích thước bằng 0 hoặc âm."
    return None


def _high_offset_detail(element):
    for builtin_name in (
        "RBS_OFFSET_PARAM",
        "INSTANCE_FREE_HOST_OFFSET_PARAM",
        "INSTANCE_ELEVATION_PARAM",
    ):
        parameter = _get_parameter(element, builtin_name)
        if parameter is None:
            continue
        try:
            if parameter.StorageType != DB.StorageType.Double:
                continue
            value = parameter.AsDouble()
            millimetres = DB.UnitUtils.ConvertFromInternalUnits(
                value, DB.UnitTypeId.Millimeters
            )
            if abs(millimetres) > 10000.0:
                return u"{0}: {1:,.0f} mm (ngưỡng kiểm tra ±10,000 mm).".format(
                    _parameter_display(parameter) or builtin_name,
                    millimetres,
                )
            return None
        except Exception:
            continue
    return None


def _scan_element(document, element, report, include_unused_equipment):
    spec = _category_spec(element)
    if spec is None:
        return
    category = spec["label"]
    element_type = _element_type_name(document, element)
    level = _level_name(document, element)
    system = _system_name(document, element, spec)

    report.category_counts[category] += 1
    report.level_counts[level] += 1
    report.system_counts[system] += 1

    should_check_connectors = spec["role"] in ("curve", "fitting", "accessory")
    if include_unused_equipment and spec["role"] == "equipment":
        should_check_connectors = True
    if should_check_connectors:
        checked, open_count = _connector_state(element)
        if checked:
            report.connector_owners += 1
            report.connectors_checked += checked
        if open_count:
            if spec["role"] == "curve":
                if spec["discipline"] in ("Piping", "HVAC"):
                    severity = "Critical"
                    action = u"Kiểm tra hai đầu tuyến và nối vào fitting/equipment hoặc đặt cap phù hợp."
                else:
                    severity = "Warning"
                    action = u"Xác nhận đầu Conduit/Cable Tray được chừa có chủ ý hoặc nối tiếp tuyến."
            elif spec["role"] in ("fitting", "accessory"):
                severity = "Warning"
                action = u"Kiểm tra hướng connector và kết nối lại fitting/accessory."
            else:
                severity = "FYI"
                action = u"Xác nhận connector này được chừa dự phòng hay cần kết nối."
            report.add_issue(
                HealthIssue(
                    severity,
                    "OPEN_CONNECTOR",
                    [element.Id],
                    category,
                    element_type,
                    level,
                    system,
                    u"{0}/{1} connector vật lý đang hở.".format(open_count, checked),
                    action,
                )
            )

    if spec["role"] == "curve" and spec["discipline"] in ("Piping", "HVAC"):
        if _system_is_missing(system):
            report.add_issue(
                HealthIssue(
                    "Warning",
                    "MISSING_SYSTEM",
                    [element.Id],
                    category,
                    element_type,
                    level,
                    system,
                    u"Không xác định được System Type/System Name.",
                    u"Gán đúng Pipe/Duct System Type trước khi tính toán hoặc xuất bản vẽ.",
                )
            )

    if spec["role"] == "curve":
        size_problem = _curve_size_problem(element, spec)
        if size_problem:
            report.add_issue(
                HealthIssue(
                    "Critical",
                    "INVALID_SIZE",
                    [element.Id],
                    category,
                    element_type,
                    level,
                    system,
                    size_problem,
                    u"Chọn lại type/size hợp lệ cho tuyến MEP.",
                )
            )

    offset_detail = _high_offset_detail(element)
    if offset_detail:
        report.add_issue(
            HealthIssue(
                "FYI",
                "HIGH_OFFSET",
                [element.Id],
                category,
                element_type,
                level,
                system,
                offset_detail,
                u"Xác nhận Level tham chiếu và cao độ thiết kế của phần tử.",
            )
        )


def _tagged_local_ids(document, active_view):
    tagged = set()
    try:
        tags = (
            DB.FilteredElementCollector(document, active_view.Id)
            .OfClass(DB.IndependentTag)
            .WhereElementIsNotElementType()
            .ToElements()
        )
    except Exception:
        return tagged, False
    for tag in tags:
        try:
            for element_id in tag.GetTaggedLocalElementIds():
                value = _category_id_value(element_id)
                if value is not None:
                    tagged.add(value)
            continue
        except Exception:
            pass
        try:
            tagged_id = tag.TaggedLocalElementId
            value = _category_id_value(tagged_id)
            if value is not None and not _is_invalid_id(tagged_id):
                tagged.add(value)
        except Exception:
            pass
    return tagged, True


def _scan_missing_tags(document, active_view, report, scope_name, scoped_elements):
    if active_view is None or active_view.IsTemplate:
        report.notes.append(u"Không kiểm tra tag vì không có model view đang hoạt động.")
        return
    tagged_ids, valid_view = _tagged_local_ids(document, active_view)
    if not valid_view:
        report.notes.append(u"View hiện tại không hỗ trợ kiểm tra Independent Tag.")
        return

    allowed_ids = None
    if scope_name == SCOPE_SELECTION:
        allowed_ids = set(_category_id_value(element.Id) for element in scoped_elements)

    candidates = []
    seen = set()
    for spec in CATEGORY_SPECS:
        if not spec["taggable"]:
            continue
        for element in _collector(document, spec, active_view.Id):
            value = _category_id_value(element.Id)
            if allowed_ids is not None and value not in allowed_ids:
                continue
            if value in seen:
                continue
            seen.add(value)
            candidates.append(element)

    report.tag_candidates = len(candidates)
    for element in candidates:
        element_id_value = _category_id_value(element.Id)
        if element_id_value in tagged_ids:
            continue
        spec = _category_spec(element)
        report.add_issue(
            HealthIssue(
                "Warning",
                "MISSING_TAG",
                [element.Id],
                spec["label"],
                _element_type_name(document, element),
                _level_name(document, element),
                _system_name(document, element, spec),
                u"Phần tử đang hiển thị nhưng chưa có Independent Tag trong view “{0}”.".format(
                    active_view.Name
                ),
                u"Dùng Universal Auto Tag MEP hoặc Smart MEP Annotation Pack để tạo tag.",
            )
        )


def _scan_revit_warnings(document, relevant_id_values, report):
    try:
        warnings = list(document.GetWarnings())
    except Exception:
        report.notes.append(u"Không đọc được Revit warnings của tài liệu.")
        return
    report.revit_warnings_checked = len(warnings)
    for warning in warnings:
        try:
            failing_ids = list(warning.GetFailingElements())
        except Exception:
            failing_ids = []
        try:
            additional_ids = list(warning.GetAdditionalElements())
        except Exception:
            additional_ids = []
        related_ids = []
        seen = set()
        for element_id in failing_ids + additional_ids:
            value = _category_id_value(element_id)
            if value not in relevant_id_values or value in seen:
                continue
            seen.add(value)
            related_ids.append(element_id)
        if not related_ids:
            continue
        try:
            description = warning.GetDescriptionText()
        except Exception:
            description = u"Revit warning"
        report.add_issue(
            HealthIssue(
                "Warning",
                "REVIT_WARNING",
                related_ids,
                u"Multiple MEP categories",
                u"—",
                u"—",
                u"—",
                description,
                u"Bấm link phần tử, dùng Review Warnings của Revit và xử lý nguyên nhân gốc.",
            )
        )


def scan_document(
    document,
    active_view,
    scope_name,
    selection_ids,
    include_tags,
    include_warnings,
    include_unused_equipment,
):
    started = time.time()
    report = HealthReport(scope_name)
    elements = _collect_elements(document, scope_name, active_view, selection_ids)
    report.elements = elements

    total = max(1, len(elements))
    with forms.ProgressBar(
        title=u"TungMEP MEP Health — {value}/{max_value}",
        cancellable=True,
        step=1,
    ) as progress:
        for index, element in enumerate(elements):
            if progress.cancelled:
                return None
            _scan_element(document, element, report, include_unused_equipment)
            progress.update_progress(index + 1, total)

    if include_tags:
        _scan_missing_tags(document, active_view, report, scope_name, elements)
    if include_warnings:
        relevant_ids = set(_category_id_value(element.Id) for element in elements)
        _scan_revit_warnings(document, relevant_ids, report)

    report.elapsed_seconds = time.time() - started
    return report


def _clean_cell(value):
    if value is None:
        return u"—"
    try:
        text = unicode(value)  # noqa: F821 - IronPython 2
    except Exception:
        text = str(value)
    return text.replace("|", "/").replace("\r", " ").replace("\n", " ")


def _status(report):
    critical = report.count_severity("Critical")
    warning = report.count_severity("Warning")
    if critical:
        return u"Cần xử lý ngay", "bad"
    if warning:
        return u"Cần kiểm tra", "warn"
    return u"Tốt", "good"


def _print_cards(output, report):
    status_text, status_class = _status(report)
    cards = [
        (u"Trạng thái", status_text, status_class),
        (u"MEP đã quét", "{0:,}".format(len(report.elements)), "neutral"),
        (u"Critical", "{0:,}".format(report.count_severity("Critical")), "bad"),
        (u"Warning", "{0:,}".format(report.count_severity("Warning")), "warn"),
        (u"FYI", "{0:,}".format(report.count_severity("FYI")), "info"),
        (u"Connector hở", "{0:,}".format(report.count_code("OPEN_CONNECTOR")), "neutral"),
        (u"Thiếu System", "{0:,}".format(report.count_code("MISSING_SYSTEM")), "neutral"),
        (u"Thiếu tag", "{0:,}".format(report.count_code("MISSING_TAG")), "neutral"),
    ]
    html = [u'<div class="tm-cards">']
    for label, value, css_class in cards:
        html.append(
            u'<div class="tm-card {0}"><div class="tm-value">{1}</div>'
            u'<div class="tm-label">{2}</div></div>'.format(css_class, value, label)
        )
    html.append(u"</div>")
    output.print_html(u"".join(html))


def _print_severity_chart(output, report):
    values = [
        report.count_severity("Critical"),
        report.count_severity("Warning"),
        report.count_severity("FYI"),
    ]
    if not any(values):
        return
    try:
        chart = output.make_doughnut_chart()
        chart.options.title = {"display": True, "text": u"Phân bố mức độ lỗi"}
        chart.data.labels = ["Critical", "Warning", "FYI"]
        dataset = chart.data.new_dataset("Issues")
        dataset.data = values
        dataset.backgroundColor = [
            SEVERITY_COLORS["Critical"],
            SEVERITY_COLORS["Warning"],
            SEVERITY_COLORS["FYI"],
        ]
        chart.set_height(230)
        chart.draw()
    except Exception:
        pass


def _issue_element_link(output, issue):
    valid_ids = [element_id for element_id in issue.elements if not _is_invalid_id(element_id)]
    if not valid_ids:
        return u"—"
    title = u"Chọn {0}".format(len(valid_ids)) if len(valid_ids) > 1 else u"Chọn/Zoom"
    try:
        return output.linkify(valid_ids, title=title)
    except Exception:
        return u", ".join(str(_category_id_value(element_id)) for element_id in valid_ids)


def _print_issue_summary(output, report):
    grouped = collections.defaultdict(list)
    for issue in report.issues:
        grouped[issue.code].append(issue)
    rows = []
    for code, issues in sorted(
        grouped.items(), key=lambda item: (-len(item[1]), CHECK_NAMES.get(item[0], item[0]))
    ):
        severity_counts = collections.Counter(issue.severity for issue in issues)
        rows.append(
            [
                CHECK_NAMES.get(code, code),
                len(issues),
                severity_counts.get("Critical", 0),
                severity_counts.get("Warning", 0),
                severity_counts.get("FYI", 0),
            ]
        )
    if rows:
        output.print_table(
            rows,
            columns=[u"Nhóm kiểm tra", u"Tổng", u"Critical", u"Warning", u"FYI"],
            title=u"Tổng hợp theo loại lỗi",
        )


def _print_issue_details(output, report):
    issues = sorted(
        report.issues,
        key=lambda issue: (
            SEVERITY_ORDER.get(issue.severity, 9),
            CHECK_NAMES.get(issue.code, issue.code),
            issue.category,
        ),
    )
    maximum_rows = 500
    rows = []
    for issue in issues[:maximum_rows]:
        rows.append(
            [
                issue.severity,
                CHECK_NAMES.get(issue.code, issue.code),
                _issue_element_link(output, issue),
                _clean_cell(issue.category),
                _clean_cell(issue.element_type),
                _clean_cell(issue.level),
                _clean_cell(issue.system),
                _clean_cell(issue.detail),
                _clean_cell(issue.suggestion),
            ]
        )
    if rows:
        output.print_table(
            rows,
            columns=[
                u"Mức độ",
                u"Kiểm tra",
                u"Phần tử",
                u"Category",
                u"Family / Type",
                u"Level",
                u"System",
                u"Chi tiết",
                u"Đề xuất xử lý",
            ],
            title=u"Chi tiết cần xử lý",
        )
    if len(issues) > maximum_rows:
        output.print_md(
            u"> Dashboard đang hiển thị {0:,}/{1:,} dòng để giữ tốc độ. "
            u"Hãy quét theo view hoặc selection để xem phần còn lại.".format(
                maximum_rows, len(issues)
            )
        )


def _print_breakdown(output, report, title, counts, issue_attribute):
    issue_counts = collections.Counter()
    for issue in report.issues:
        issue_counts[getattr(issue, issue_attribute)] += 1
    rows = []
    for name, element_count in sorted(
        counts.items(), key=lambda item: (-item[1], item[0])
    ):
        rows.append([_clean_cell(name), element_count, issue_counts.get(name, 0)])
    if rows:
        output.print_table(
            rows[:100],
            columns=[title, u"Số phần tử", u"Số vấn đề"],
            title=u"Phân bố theo {0}".format(title),
        )


def render_dashboard(document, active_view, report):
    output = script.get_output()
    output.close_others()
    output.set_title(u"TungMEP — MEP Project Health Dashboard")
    try:
        output.set_width(1500)
        output.set_height(900)
    except Exception:
        pass
    output.add_style(
        """
        body { font-family: 'Segoe UI', Arial, sans-serif; }
        .tm-cards { display:flex; flex-wrap:wrap; gap:10px; margin:12px 0 20px 0; }
        .tm-card { min-width:128px; padding:12px 15px; border-radius:6px;
                   border-left:5px solid #6b7280; background:#f3f4f6; }
        .tm-card.bad { border-color:#d83b32; background:#fdeceb; }
        .tm-card.warn { border-color:#f2a900; background:#fff7d6; }
        .tm-card.good { border-color:#16825d; background:#e8f6ef; }
        .tm-card.info { border-color:#2878b5; background:#eaf3fa; }
        .tm-value { font-size:22px; font-weight:700; color:#1f2937; }
        .tm-label { font-size:11px; color:#4b5563; margin-top:3px; }
        table { font-size:11px; }
        th { background:#154d7d; color:white; }
        tr:nth-child(even) { background:#f5f7fa; }
        """
    )

    output.print_md(u"# MEP Project Health Dashboard")
    output.print_md(
        u"**Dự án:** {0}  \n**Phạm vi:** {1}  \n**View kiểm tra tag:** {2}  "
        u"\n**Thời điểm:** {3}  \n**Thời gian quét:** {4:.1f} giây".format(
            document.Title,
            report.scope_name,
            active_view.Name if active_view is not None else u"—",
            datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
            report.elapsed_seconds,
        )
    )
    output.print_md(
        u"> Dashboard chỉ đọc dữ liệu. Bấm **Chọn/Zoom** để Revit chọn và đưa phần tử vào khung nhìn; tool không tự sửa hoặc xóa model."
    )
    _print_cards(output, report)

    if not report.issues:
        output.print_md(u"## Không phát hiện vấn đề trong phạm vi đã chọn.")
    else:
        _print_severity_chart(output, report)
        output.print_md(u"## Tổng hợp")
        _print_issue_summary(output, report)
        output.print_md(u"## Danh sách chi tiết")
        _print_issue_details(output, report)

    output.print_md(u"## Thống kê model")
    output.print_md(
        u"- Connector owners đã kiểm tra: **{0:,}**  \n"
        u"- Connector vật lý đã kiểm tra: **{1:,}**  \n"
        u"- Đối tượng có thể tag trong view: **{2:,}**  \n"
        u"- Revit warnings đã đọc: **{3:,}**".format(
            report.connector_owners,
            report.connectors_checked,
            report.tag_candidates,
            report.revit_warnings_checked,
        )
    )
    _print_breakdown(output, report, u"Category", report.category_counts, "category")
    _print_breakdown(output, report, u"Level", report.level_counts, "level")
    _print_breakdown(output, report, u"System", report.system_counts, "system")

    if report.notes:
        output.print_md(u"## Ghi chú")
        for note in report.notes:
            output.print_md(u"- " + note)


def _choose_scan_options():
    result = forms.CommandSwitchWindow.show(
        [SCOPE_PROJECT, SCOPE_VIEW, SCOPE_SELECTION],
        switches={
            CHECK_TAGS: True,
            CHECK_WARNINGS: True,
            CHECK_UNUSED_EQUIPMENT: False,
        },
        message=u"Chọn phạm vi MEP Project Health Dashboard",
        recognize_access_key=False,
    )
    if not result:
        return None
    scope_name, switches = result
    if not scope_name:
        return None
    return (
        scope_name,
        bool(switches.get(CHECK_TAGS, True)),
        bool(switches.get(CHECK_WARNINGS, True)),
        bool(switches.get(CHECK_UNUSED_EQUIPMENT, False)),
    )


def run():
    document = revit.doc
    uidocument = revit.uidoc
    if document is None or uidocument is None:
        forms.alert(
            u"Hãy mở một Revit project trước khi chạy MEP Health Dashboard.",
            title=u"TungMEP — MEP Health",
        )
        return
    if document.IsFamilyDocument:
        forms.alert(
            u"Dashboard chỉ chạy trong Revit project, không chạy trong Family Editor.",
            title=u"TungMEP — MEP Health",
        )
        return

    options = _choose_scan_options()
    if options is None:
        return
    scope_name, include_tags, include_warnings, include_unused_equipment = options
    selection_ids = list(uidocument.Selection.GetElementIds())
    if scope_name == SCOPE_SELECTION and not selection_ids:
        forms.alert(
            u"Chưa có đối tượng MEP nào được chọn. Hãy chọn trước rồi chạy lại, hoặc dùng phạm vi View/Dự án.",
            title=u"TungMEP — MEP Health",
        )
        return

    report = scan_document(
        document,
        document.ActiveView,
        scope_name,
        selection_ids,
        include_tags,
        include_warnings,
        include_unused_equipment,
    )
    if report is None:
        return
    if not report.elements and scope_name == SCOPE_SELECTION:
        forms.alert(
            u"Selection hiện tại không chứa category MEP được dashboard hỗ trợ.",
            title=u"TungMEP — MEP Health",
        )
        return
    render_dashboard(document, document.ActiveView, report)
