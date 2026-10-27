import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import '../ui/ui.css';
import './admin.css';
import './theme.css';
import { AdminApp } from './AdminApp';

createRoot(document.getElementById('root')!).render(<StrictMode><AdminApp /></StrictMode>);
