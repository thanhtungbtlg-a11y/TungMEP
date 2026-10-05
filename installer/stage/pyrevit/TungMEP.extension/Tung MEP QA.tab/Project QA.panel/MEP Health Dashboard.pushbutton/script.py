# -*- coding: utf-8 -*-

from pyrevit import forms

from mep_health_dashboard import run


try:
    run()
except Exception as exception:
    forms.alert(
        u"Không thể hoàn tất MEP Health Dashboard.\n\n{0}".format(exception),
        title=u"TungMEP — MEP Health",
    )

