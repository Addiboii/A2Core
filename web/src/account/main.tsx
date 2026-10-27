import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import '../ui/ui.css';
import './account.css';
import { AccountApp } from './AccountApp';

createRoot(document.getElementById('root')!).render(<StrictMode><AccountApp /></StrictMode>);
