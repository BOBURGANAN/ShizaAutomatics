;;; B0 - Move selected block contents to layer 0, including nested blocks.
;;; Block insertions selected by the user retain their original layer.
;;; Shared block definitions affect every insertion in this drawing.
(vl-load-com)

(defun shiza:run-b0 (/ *error* doc blocks ss visited changed failed skipped undo
               setzero attributes walk i obj result)
  (setq doc (vla-get-ActiveDocument (vlax-get-acad-object))
        blocks (vla-get-Blocks doc)
        visited nil changed 0 failed 0 skipped 0)

  (defun *error* (msg)
    (if undo (vla-EndUndoMark doc))
    (if msg (princ (strcat "\nB0: " msg)))
    (princ))

  (defun setzero (entity / old res)
    (setq old (vl-catch-all-apply 'vla-get-Layer (list entity)))
    (if (not (vl-catch-all-error-p old))
      (if (/= old "0")
        (progn
          (setq res (vl-catch-all-apply 'vla-put-Layer (list entity "0")))
          (if (vl-catch-all-error-p res)
            (setq failed (1+ failed))
            (setq changed (1+ changed)))))))

  (defun attributes (ref / attrs att)
    (if (and (vlax-property-available-p ref 'HasAttributes)
             (= :vlax-true (vla-get-HasAttributes ref)))
      (progn
        (setq attrs (vl-catch-all-apply 'vlax-invoke (list ref 'GetAttributes)))
        (if (vl-catch-all-error-p attrs)
          (setq failed (1+ failed))
          (foreach att attrs (setzero att))))))

  (defun walk (name / key definition entity)
    (setq key (strcase name))
    (if (not (member key visited))
      (progn
        (setq visited (cons key visited)
              definition (vl-catch-all-apply 'vla-Item (list blocks name)))
        (cond
          ((vl-catch-all-error-p definition) (setq failed (1+ failed)))
          ((= :vlax-true (vla-get-IsXRef definition))
           (setq skipped (1+ skipped)))
          (T
           (vlax-for entity definition
             (setzero entity)
             (if (= "AcDbBlockReference" (vla-get-ObjectName entity))
               (progn
                 (attributes entity)
                 (walk (vla-get-Name entity))))))))))

  (princ "\nSelect blocks whose contents should move to layer 0: ")
  (if (setq ss (ssget '((0 . "INSERT"))))
    (progn
      (vla-StartUndoMark doc)
      (setq undo T i 0)
      (repeat (sslength ss)
        (setq obj (vlax-ename->vla-object (ssname ss i))
              i (1+ i))
        ;; External references are deliberately left untouched.
        (setq result (vla-Item blocks (vla-get-Name obj)))
        (if (= :vlax-true (vla-get-IsXRef result))
          (setq skipped (1+ skipped))
          (progn
            (attributes obj)
            (walk (vla-get-Name obj)))))
      (vla-Regen doc 1)
      (vla-EndUndoMark doc)
      (setq undo nil)
      (princ (strcat "\nB0 complete. Objects changed: " (itoa changed)
                     ". Failed: " (itoa failed)
                     ". Xrefs skipped: " (itoa skipped) "."))
      (princ "\nShared definitions updated. Use UNDO to revert."))
    (princ "\nNo blocks selected."))
  (princ))

(princ)
