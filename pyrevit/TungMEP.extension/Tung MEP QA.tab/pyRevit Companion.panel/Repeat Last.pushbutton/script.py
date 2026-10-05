# -*- coding: utf-8 -*-

from pyrevit import forms

from tungmep_bridge import repeat_last_command


try:
    repeat_last_command()
except Exception as error:
    forms.alert(
        str(error),
        title="TungMEP - Repeat Last",
        warn_icon=True,
    )
