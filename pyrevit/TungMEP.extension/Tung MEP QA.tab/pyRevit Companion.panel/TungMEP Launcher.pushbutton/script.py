# -*- coding: utf-8 -*-

from collections import OrderedDict

from pyrevit import forms

from tungmep_bridge import execute_payload_command


COMMANDS = OrderedDict([
    ("Auto Sprinkler", "TungMEP.Modules.AutoSprinkler.AutoSprinklerCommand"),
    ("Setup Sprinkler Styles", "TungMEP.Modules.AutoSprinkler.CreateBoundaryStyleCommand"),
    ("Grid Mode", "TungMEP.Modules.Grids.GridModeCommand"),
    ("Drafting to Legend", "TungMEP.Modules.Views.DraftingToLegendCommand"),
    ("Auto Dimension", "TungMEP.Modules.Dimensions.AutoDimensionCommand"),
    ("Smart Auto Tag Pipe", "TungMEP.Modules.Tags.AutoTagCommand"),
    ("Universal Auto Tag MEP", "TungMEP.Modules.Tags.UniversalMepAutoTagCommand"),
    ("Smart Annotation Arrange", "TungMEP.Modules.Tags.AnnotationArrangeCommand"),
    ("Smart MEP Annotation Pack", "TungMEP.Modules.Tags.SmartMepAnnotationCommand"),
    ("MEP SLIPT BY LEVEL", "TungMEP.Modules.MepTools.MepSplitCommand"),
    ("Batch View Copy", "TungMEP.Modules.Views.BatchViewCopyCommand"),
    ("View & Sheet Automation", "TungMEP.Modules.Views.ViewSheetAutomationCommand"),
    ("Sheet Renumber", "TungMEP.Modules.Views.SheetRenumberCommand"),
])


selected = forms.CommandSwitchWindow.show(
    list(COMMANDS.keys()),
    message="Search and run a TungMEP command",
)
if selected:
    try:
        execute_payload_command(COMMANDS[selected], remember=True)
    except Exception as error:
        forms.alert(
            str(error),
            title="TungMEP Launcher",
            warn_icon=True,
        )
