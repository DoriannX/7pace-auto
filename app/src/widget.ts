import './theme.css'
import { mount } from 'svelte'
import Widget from './Widget.svelte'

mount(Widget, { target: document.getElementById('app')! })
