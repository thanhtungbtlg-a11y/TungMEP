# -*- coding: utf-8 -*-

from pyrevit import forms

from tungmep_bridge import reload_payload


try:
    reload_payload()
except Exception as error:
    forms.alert(
        str(error),
        title="TungMEP - Reload Payload",
        warn_icon=True,
    )
