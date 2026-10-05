# -*- coding: utf-8 -*-

from pyrevit import EXEC_PARAMS, HOST_APP, revit, script

from tungmep_bridge import diagnostics


data = diagnostics()
output = script.get_output()
output.close_others()
output.print_md("# TungMEP + pyRevit Diagnostics")
output.print_md("- **Revit:** {0}".format(HOST_APP.version_name))
output.print_md("- **pyRevit engine:** {0}".format(EXEC_PARAMS.engine_ver))
output.print_md(
    "- **Active document:** {0}".format(
        revit.doc.Title if revit.doc is not None else "No document"
    )
)
output.print_md(
    "- **TungMEP loader:** {0} ({1})".format(
        "Loaded" if data["loader_loaded"] else "Not loaded",
        data["loader_version"],
    )
)
output.print_md(
    "- **Current payload:** {0}".format(
        data["payload_path"] if data["payload_exists"] else "Not found"
    )
)
output.print_md(
    "- **Payload already loaded in session:** {0}".format(
        "Yes" if data["payload_loaded"] else "No"
    )
)
output.print_md("- **Last TungMEP command:** `{0}`".format(data["last_command"]))
output.print_md("- **History file:** `{0}`".format(data["history_path"]))
output.print_md("- **Install root:** `{0}`".format(data["install_root"]))

if data["loader_loaded"] and data["payload_exists"]:
    output.print_md("\n## Status: Ready")
else:
    output.print_md("\n## Status: Needs attention")
    output.print_md(
        "Restart Revit after installing TungMEP, or deploy a current payload before using the companion commands."
    )
