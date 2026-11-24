;;; SHIZALEVELS - Calculate elevations from the vertical offset to a reference.
(vl-load-com)

(defun shiza-level:parse (text / mark minus sign body dot digits n ch valid)
  (setq text (vl-string-trim " \t\r\n" text)
        mark (substr text 1 1)
        minus (chr (if (= (getvar "LISPSYS") 0) 150 8722))
        sign (if (member mark (list "-" minus)) -1.0 1.0)
        body (if (member mark (list "+" "-" minus "±"))
               (vl-string-left-trim " \t" (substr text 2))
               text)
        body (vl-string-translate "," "." body)
        n 1
        digits 0
        valid T)
  (while (and valid (<= n (strlen body)))
    (setq ch (substr body n 1))
    (cond
      ((wcmatch ch "#") (setq digits (1+ digits)))
      ((and (= ch ".") (not dot)) (setq dot n))
      (T (setq valid nil)))
    (setq n (1+ n)))
  (if (and valid (> digits 0)
           (or (/= mark "±") (= (atof body) 0.0))
           (or (not dot) (<= (- (strlen body) dot) 8)))
    (list (* sign (atof body))
          (if dot (- (strlen body) dot) 0)
          (if (vl-string-search "." text) "." ","))))

(defun shiza-level:format (value precision separator / number dot)
  ;; rtos may omit leading or trailing zeroes according to DIMZIN.
  (setq number (vl-string-translate "," "." (rtos (abs value) 2 precision)))
  (cond ((= number "") (setq number "0"))
        ((= (substr number 1 1) ".") (setq number (strcat "0" number))))
  (if (not (setq dot (vl-string-position 46 number)))
    (setq dot (strlen number)
          number (strcat number ".")))
  (while (< (- (strlen number) dot 1) precision)
    (setq number (strcat number "0")))
  (strcat (cond ((= (atof number) 0.0) "")
                ((< value 0.0) "-")
                (T "+"))
          (vl-string-translate "." separator number)))

(defun shiza-level:attribute (block / data next result)
  (setq data (entget block))
  (if (and (= (cdr (assoc 0 data)) "INSERT") (= (cdr (assoc 66 data)) 1))
    (progn
      (setq next (entnext block))
      (while (and next (not result)
                  (= "ATTRIB" (cdr (assoc 0 (setq data (entget next))))))
        (if (= (strcase (cdr (assoc 2 data))) "ОТМЕТКА")
          (setq result next)
          (setq next (entnext next))))))
  result)

(defun shiza-level:update (block data text / changed)
  (setq changed (vl-catch-all-apply 'entmod
    (list (subst (cons 1 text) (assoc 1 data) data))))
  (if (and changed (not (vl-catch-all-error-p changed)))
    (progn (entupd block) T)))

(defun shiza-level:y (block)
  (cadr (trans (cdr (assoc 10 (entget block))) block 0)))

(defun shiza-level:meters-per-unit ()
  ;; INSUNITS indices. A unitless drawing uses millimeters by default.
  (nth (getvar "INSUNITS")
    '(0.001 0.0254 0.3048 1609.344 0.001 0.01 1.0 1000.0
      2.54e-8 2.54e-5 0.9144 1.0e-10 1.0e-9 1.0e-6 0.1 10.0 100.0
      1.0e9 149597870700.0 9.4607304725808e15 3.0856775814913673e16
      0.3048006096012192 0.0254000508001016 0.9144018288036576 1609.347218694437)))

(defun shiza:run-levels (/ *error* doc undo pick base attribute reference base-y unit selection
                       index block data old parsed precision separator height new updated skipped unchanged)
  (setq doc (vla-get-ActiveDocument (vlax-get-acad-object)))
  (defun *error* (msg)
    (if undo (vla-EndUndoMark doc))
    (if (and msg (not (wcmatch (strcase msg) "*CANCEL*,*QUIT*,*BREAK*")))
      (princ (strcat "\nSHIZALEVELS: " msg)))
    (princ))
  (cond
    ((not (setq pick (entsel "\nВыберите опорную высотную отметку: "))))
    ((not (and (setq base (car pick)
                     attribute (shiza-level:attribute base))
               (setq reference (shiza-level:parse (cdr (assoc 1 (entget attribute)))))))
     (princ "\nВ опорном блоке нужна числовая отметка в атрибуте ОТМЕТКА."))
    ((not (setq unit (shiza-level:meters-per-unit)))
     (princ "\nНе удалось определить единицы чертежа (INSUNITS)."))
    (T
     (setq base-y (shiza-level:y base))
     (princ "\nВыберите высотные отметки для пересчёта, затем нажмите Enter: ")
     (if (setq selection (ssget '((0 . "INSERT") (66 . 1))))
       (progn
         (setq index 0 updated 0 skipped 0 unchanged 0)
         (vla-StartUndoMark doc)
         (setq undo T)
         (repeat (sslength selection)
           (setq block (ssname selection index)
                 index (1+ index))
           (cond
             ((eq block base))
             ((setq attribute (shiza-level:attribute block))
              (setq data (entget attribute)
                    old (assoc 1 data)
                    parsed (shiza-level:parse (cdr old))
                    precision (max 3 (cadr reference) (if parsed (cadr parsed) 0))
                    separator (if parsed (caddr parsed) (caddr reference))
                    height (+ (car reference) (* (- (shiza-level:y block) base-y) unit))
                    new (shiza-level:format height precision separator))
              (cond
                ((= new (cdr old)) (setq unchanged (1+ unchanged)))
                ((shiza-level:update block data new)
                 (setq updated (1+ updated)))
                (T (setq skipped (1+ skipped)))))
             (T (setq skipped (1+ skipped)))))
         (vla-EndUndoMark doc)
         (setq undo nil)
         (princ (strcat "\nПересчитано: " (itoa updated)
                        ". Без изменений: " (itoa unchanged)
                        ". Пропущено: " (itoa skipped) "."))))))
  (princ))

(princ)
